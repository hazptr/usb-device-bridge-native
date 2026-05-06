using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Information about a release discovered on GitHub.
/// </summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleasePageUrl,
    string? MsiAssetUrl,
    string? MsiAssetName,
    string? Sha256AssetUrl,
    string? ReleaseNotes);

/// <summary>
/// Result of an update check.
/// </summary>
public enum UpdateCheckOutcome
{
    NoUpdate,
    UpdateAvailable,
    Failed,
}

public sealed class UpdateCheckResult
{
    public UpdateCheckOutcome Outcome { get; init; }
    public UpdateInfo? Update { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Polls GitHub releases for new versions of the app, downloads them in the background
/// when configured, and provides events the UI can hook into to surface install prompts
/// or notifications.
/// </summary>
public sealed class UpdateCheckService : IDisposable
{
    private const string GithubApiUrl = "https://api.github.com/repos/delabrcd/usb-device-bridge-native/releases/latest";
    private const string ReleasesPageUrl = "https://github.com/delabrcd/usb-device-bridge-native/releases";

    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);

    private readonly Func<string> _getMode;
    private readonly Version _currentVersion;
    private readonly HttpClient _http;
    private readonly string _downloadDir;
    private readonly object _gate = new();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private UpdateInfo? _lastFoundUpdate;
    private string? _lastDownloadedFile;

    /// <summary>Raised when an update is found in <see cref="UpdateCheckModes.Notify"/> mode.</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>Raised when an update has been fully downloaded in <see cref="UpdateCheckModes.Automatic"/> mode.</summary>
    public event Action<UpdateInfo, string>? UpdateDownloaded;

    /// <summary>Raised when a check or download fails. Surfaced for diagnostics — UI may ignore.</summary>
    public event Action<string>? CheckFailed;

    public UpdateCheckService(Func<string> getMode, Version? currentVersionOverride = null)
    {
        _getMode = getMode ?? throw new ArgumentNullException(nameof(getMode));
        _currentVersion = currentVersionOverride ?? GetEntryAssemblyVersion();

        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
            "UsbDeviceBridge",
            _currentVersion.ToString()));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        _downloadDir = Path.Combine(localAppData, "UsbDeviceBridge", "updates");
        Directory.CreateDirectory(_downloadDir);
    }

    public Version CurrentVersion => _currentVersion;

    /// <summary>The most recent update that has been downloaded and is ready to install, if any.</summary>
    public (UpdateInfo Info, string FilePath)? PendingInstall
    {
        get
        {
            lock (_gate)
            {
                if (_lastFoundUpdate is null || _lastDownloadedFile is null)
                    return null;
                if (!File.Exists(_lastDownloadedFile))
                    return null;
                return (_lastFoundUpdate, _lastDownloadedFile);
            }
        }
    }

    /// <summary>
    /// Starts the background polling loop. Safe to call multiple times — additional calls are ignored.
    /// The loop respects the current mode at each iteration, so toggling the mode in settings takes
    /// effect on the next tick.
    /// </summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_loopTask is not null)
                return;

            _loopCts = new CancellationTokenSource();
            _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate)
        {
            cts = _loopCts;
            _loopCts = null;
            _loopTask = null;
        }

        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch { }
    }

    /// <summary>
    /// Runs a single check immediately, regardless of whether the loop is running.
    /// Honours <see cref="UpdateCheckModes.Disabled"/> by returning <see cref="UpdateCheckOutcome.NoUpdate"/>.
    /// In <see cref="UpdateCheckModes.Automatic"/> mode, the asset is downloaded if found.
    /// </summary>
    public async Task<UpdateCheckResult> CheckNowAsync(CancellationToken ct)
    {
        var mode = UpdateCheckModes.Normalize(_getMode());
        if (string.Equals(mode, UpdateCheckModes.Disabled, StringComparison.Ordinal))
            return new UpdateCheckResult { Outcome = UpdateCheckOutcome.NoUpdate };

        try
        {
            var update = await FetchLatestReleaseAsync(ct);
            if (update is null || update.Version <= _currentVersion)
                return new UpdateCheckResult { Outcome = UpdateCheckOutcome.NoUpdate };

            lock (_gate)
            {
                _lastFoundUpdate = update;
            }

            if (string.Equals(mode, UpdateCheckModes.Notify, StringComparison.Ordinal))
            {
                UpdateAvailable?.Invoke(update);
                return new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpdateAvailable, Update = update };
            }

            if (string.IsNullOrEmpty(update.MsiAssetUrl) || string.IsNullOrEmpty(update.MsiAssetName))
            {
                // No MSI asset on this release — fall back to notify behaviour so the user still sees it.
                UpdateAvailable?.Invoke(update);
                return new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpdateAvailable, Update = update };
            }

            var existing = TryFindAlreadyDownloaded(update.TagName, update.MsiAssetName);
            if (existing is not null)
            {
                lock (_gate) _lastDownloadedFile = existing;
                UpdateDownloaded?.Invoke(update, existing);
                return new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpdateAvailable, Update = update };
            }

            var downloaded = await DownloadAssetAsync(update.MsiAssetUrl, update.TagName, update.MsiAssetName, update.Sha256AssetUrl, ct);
            lock (_gate) _lastDownloadedFile = downloaded;
            UpdateDownloaded?.Invoke(update, downloaded);
            return new UpdateCheckResult { Outcome = UpdateCheckOutcome.UpdateAvailable, Update = update };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CheckFailed?.Invoke(ex.Message);
            return new UpdateCheckResult { Outcome = UpdateCheckOutcome.Failed, ErrorMessage = ex.Message };
        }
    }

    /// <summary>
    /// Launches the MSI installer in passive mode (progress bar, no wizard pages).
    /// Returns the started Process on success, null if the file is missing or the user cancels UAC.
    /// Caller is expected to shut down the app after this returns non-null.
    /// </summary>
    public Process? LaunchInstaller(string msiPath)
    {
        if (string.IsNullOrEmpty(msiPath) || !File.Exists(msiPath))
            return null;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "msiexec",
                UseShellExecute = true,
                Verb = "runas",
            };
            psi.ArgumentList.Add("/i");
            psi.ArgumentList.Add(msiPath);
            psi.ArgumentList.Add("/passive");
            psi.ArgumentList.Add("/norestart");
            return Process.Start(psi);
        }
        catch
        {
            return null;
        }
    }

    public static string GetReleasesPageUrl() => ReleasesPageUrl;

    public void Dispose()
    {
        Stop();
        _http.Dispose();
    }

    // ---------- internals ----------

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                if (!string.Equals(_getMode(), UpdateCheckModes.Disabled, StringComparison.OrdinalIgnoreCase))
                {
                    try { await CheckNowAsync(ct); }
                    catch (OperationCanceledException) { throw; }
                    catch { /* surfaced via CheckFailed */ }
                }

                await Task.Delay(CheckInterval, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<UpdateInfo?> FetchLatestReleaseAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync(GithubApiUrl, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var release = await JsonSerializer.DeserializeAsync<GithubRelease>(stream, JsonOpts, ct);
        if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            return null;

        if (!TryParseVersion(release.TagName, out var version))
            return null;

        var msi = release.Assets?.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a.Name)
            && a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

        var sha256 = release.Assets?.FirstOrDefault(a =>
            !string.IsNullOrEmpty(a.Name)
            && a.Name.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase));

        return new UpdateInfo(
            Version: version,
            TagName: release.TagName,
            ReleasePageUrl: string.IsNullOrEmpty(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl,
            MsiAssetUrl: msi?.BrowserDownloadUrl,
            MsiAssetName: msi?.Name,
            Sha256AssetUrl: sha256?.BrowserDownloadUrl,
            ReleaseNotes: release.Body);
    }

    private string? TryFindAlreadyDownloaded(string tagName, string assetName)
    {
        try
        {
            var path = Path.Combine(_downloadDir, VersionedFileName(tagName, assetName));
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private async Task<string> DownloadAssetAsync(string url, string tagName, string assetName, string? sha256Url, CancellationToken ct)
    {
        var localName = VersionedFileName(tagName, assetName);
        var finalPath = Path.Combine(_downloadDir, localName);
        var tempPath = finalPath + ".part";

        if (File.Exists(tempPath))
        {
            try { File.Delete(tempPath); } catch { }
        }

        using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await src.CopyToAsync(dst, ct);
        }

        if (sha256Url is not null)
            await VerifySha256Async(tempPath, sha256Url, ct);

        if (File.Exists(finalPath))
        {
            try { File.Delete(finalPath); } catch { }
        }

        File.Move(tempPath, finalPath);
        PruneOldDownloads(keep: localName);
        return finalPath;
    }

    private async Task VerifySha256Async(string filePath, string sha256Url, CancellationToken ct)
    {
        var checksumText = await _http.GetStringAsync(sha256Url, ct);
        // Format written by CI: "{HASH}  {filename}"
        var expectedHash = checksumText.Trim().Split([' ', '\t'], 2)[0].ToUpperInvariant();

        using var sha = SHA256.Create();
        await using var stream = File.OpenRead(filePath);
        var actualHash = Convert.ToHexString(await sha.ComputeHashAsync(stream, ct));

        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(filePath); } catch { }
            throw new InvalidDataException(
                $"SHA256 mismatch for downloaded installer: expected {expectedHash}, got {actualHash}");
        }
    }

    private static string VersionedFileName(string tagName, string assetName) =>
        $"{tagName}_{assetName}";

    private void PruneOldDownloads(string keep)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_downloadDir, "*.msi"))
            {
                if (string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
    }

    private static Version GetEntryAssemblyVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info) && TryParseVersion(info, out var v))
            return v;
        return asm.GetName().Version ?? new Version(0, 0, 0, 0);
    }

    /// <summary>
    /// Parses a release tag (e.g. "v1.2.3", "1.2.3", "1.2.3-rc.1+build") into a 4-part Version.
    /// Pre-release suffixes are stripped before parsing.
    /// </summary>
    public static bool TryParseVersion(string raw, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var trimmed = raw.Trim();
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[1..];

        var dashIdx = trimmed.IndexOf('-');
        if (dashIdx >= 0) trimmed = trimmed[..dashIdx];
        var plusIdx = trimmed.IndexOf('+');
        if (plusIdx >= 0) trimmed = trimmed[..plusIdx];

        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 4)
            return false;

        var nums = new int[4];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var n) || n < 0)
                return false;
            nums[i] = n;
        }

        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class GithubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("body")]
        public string? Body { get; set; }

        [JsonPropertyName("assets")]
        public List<GithubAsset>? Assets { get; set; }
    }

    private sealed class GithubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
