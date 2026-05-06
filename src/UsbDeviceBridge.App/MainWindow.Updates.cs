using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using UsbDeviceBridge.App.Models;
using UsbDeviceBridge.App.Services;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing GitHub release update-check wiring for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private UpdateCheckService? _updateCheckService;
    private bool _updatePromptOpen;

    public IReadOnlyList<string> UpdateCheckModeOptions { get; } = UpdateCheckModes.All
        .Select(UpdateCheckModes.GetLabel)
        .ToList();

    public string UpdateCheckModeSelected
    {
        get => UpdateCheckModes.GetLabel(_settings.UpdateCheckMode);
        set
        {
            var rawValue = UpdateCheckModes.All
                .FirstOrDefault(m => string.Equals(UpdateCheckModes.GetLabel(m), value, StringComparison.OrdinalIgnoreCase))
                ?? UpdateCheckModes.Automatic;

            if (string.Equals(_settings.UpdateCheckMode, rawValue, StringComparison.OrdinalIgnoreCase))
                return;

            _settings.UpdateCheckMode = rawValue;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateCheckModeSelected)));
        }
    }

    private void StartUpdateChecks()
    {
        if (_updateCheckService is not null)
            return;

        _updateCheckService = new UpdateCheckService(() => _settings.UpdateCheckMode);
        _updateCheckService.UpdateAvailable += OnUpdateAvailable;
        _updateCheckService.UpdateDownloaded += OnUpdateDownloaded;
        _updateCheckService.Start();
    }

    private void StopUpdateChecks()
    {
        if (_updateCheckService is null)
            return;

        _updateCheckService.UpdateAvailable -= OnUpdateAvailable;
        _updateCheckService.UpdateDownloaded -= OnUpdateDownloaded;
        _updateCheckService.Dispose();
        _updateCheckService = null;
    }

    /// <summary>Notify-only update path: surface a notification with a link to the releases page.</summary>
    private void OnUpdateAvailable(UpdateInfo update)
    {
        Dispatcher.Invoke(() =>
        {
            _vm.NotificationService.AddNotification(
                $"Update available: {update.TagName}. Click to open the releases page.",
                NotificationSeverity.Info,
                source: "Updates");

            if (_settings.WindowsNotificationsEnabled)
            {
                _tray.ShowOsNotification(
                    "USB Device Bridge update available",
                    $"Version {update.TagName} is available. Open Settings to view.",
                    NotificationSeverity.Info);
            }
        });
    }

    /// <summary>Automatic update path: prompt the user to install the downloaded MSI.</summary>
    private void OnUpdateDownloaded(UpdateInfo update, string filePath)
    {
        Dispatcher.Invoke(() =>
        {
            _vm.NotificationService.AddNotification(
                $"Update {update.TagName} downloaded — ready to install.",
                NotificationSeverity.Info,
                source: "Updates");

            if (_settings.WindowsNotificationsEnabled)
            {
                _tray.ShowOsNotification(
                    "USB Device Bridge update ready",
                    $"Version {update.TagName} is ready to install. Click to review.",
                    NotificationSeverity.Info);
            }

            ShowInstallPromptIfWindowReady(update, filePath);
        });
    }

    private void ShowInstallPromptIfWindowReady(UpdateInfo update, string filePath)
    {
        if (_updatePromptOpen)
            return;

        // Don't pop a modal dialog over the setup wizard — we'll show it after the next launch.
        if (SetupOverlay.Visibility == Visibility.Visible)
            return;

        if (!IsLoaded || !IsVisible)
            return;

        _updatePromptOpen = true;
        try
        {
            var notes = string.IsNullOrWhiteSpace(update.ReleaseNotes)
                ? string.Empty
                : Environment.NewLine + Environment.NewLine + Truncate(update.ReleaseNotes, 600);

            var message =
                $"Version {update.TagName} is ready to install. The app will close, install silently, and relaunch automatically."
                + notes;

            var install = ShowThemedConfirmationDialog(
                "Install update",
                message,
                "Install now",
                "Later");

            if (!install)
                return;

            if (_updateCheckService is null)
                return;

            var msiProcess = _updateCheckService.LaunchInstaller(filePath);
            if (msiProcess is null)
            {
                ShowThemedNoticeDialog(
                    "Install failed",
                    "The update installer could not be launched. Open the releases page to download manually.",
                    "OK");
                return;
            }

            TryStartRelaunchHelper();

            _exitingFromTray = true;
            _tray.HideIcon();
            System.Windows.Application.Current.Shutdown();
        }
        finally
        {
            _updatePromptOpen = false;
        }
    }

    /// <summary>
    /// Manually triggered update check from the settings panel. Returns user-friendly status text.
    /// </summary>
    private async Task<string> CheckForUpdatesNowAsync()
    {
        if (_updateCheckService is null)
        {
            // Service hasn't been started yet (still in setup, etc.). Make a one-shot instance.
            using var oneShot = new UpdateCheckService(() => _settings.UpdateCheckMode);
            return await RunManualCheckAsync(oneShot);
        }

        return await RunManualCheckAsync(_updateCheckService);
    }

    private async Task<string> RunManualCheckAsync(UpdateCheckService service)
    {
        if (string.Equals(_settings.UpdateCheckMode, UpdateCheckModes.Disabled, StringComparison.OrdinalIgnoreCase))
            return "Update checks are disabled. Change the mode to enable them.";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var result = await service.CheckNowAsync(cts.Token);
            return result.Outcome switch
            {
                UpdateCheckOutcome.NoUpdate => $"You're up to date (current version {service.CurrentVersion}).",
                UpdateCheckOutcome.UpdateAvailable when result.Update is not null
                    => $"Update available: {result.Update.TagName}.",
                UpdateCheckOutcome.UpdateAvailable
                    => "Update available.",
                UpdateCheckOutcome.Failed
                    => $"Update check failed. {result.ErrorMessage}",
                _ => "Update check finished.",
            };
        }
        catch (OperationCanceledException)
        {
            return "Update check timed out.";
        }
        catch (Exception ex)
        {
            return $"Update check failed. {ex.Message}";
        }
    }

    private void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = UpdateCheckService.GetReleasesPageUrl(),
                UseShellExecute = true,
            });
        }
        catch
        {
            // Ignore — best effort.
        }
    }

    private async void CheckUpdates_OnClick(object sender, RoutedEventArgs e)
    {
        var button = SettingsOverlay.CheckUpdatesButton;
        var status = SettingsOverlay.CheckUpdatesStatus;
        var originalContent = button.Content;
        button.IsEnabled = false;
        button.Content = "Checking...";
        status.Text = "Checking for updates...";
        try
        {
            var message = await CheckForUpdatesNowAsync();
            status.Text = message;
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalContent;
        }
    }

    private void OpenReleasesPage_OnClick(object sender, RoutedEventArgs e)
    {
        OpenReleasesPage();
    }

    private static void TryStartRelaunchHelper()
    {
        try
        {
            var appExePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(appExePath))
                return;

            const string script = """
                param([string]$AppPath)
                Start-Sleep -Seconds 5
                $deadline = [datetime]::UtcNow.AddMinutes(10)
                while ([datetime]::UtcNow -lt $deadline) {
                    if (-not (Get-Process -Name msiexec -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Seconds 3
                }
                Start-Sleep -Seconds 2
                if (Test-Path $AppPath) { Start-Process $AppPath }
                """;

            var scriptPath = Path.Combine(Path.GetTempPath(), "UsbDeviceBridgeRelaunch.ps1");
            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-WindowStyle");
            psi.ArgumentList.Add("Hidden");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(scriptPath);
            psi.ArgumentList.Add("-AppPath");
            psi.ArgumentList.Add(appExePath);
            Process.Start(psi);
        }
        catch
        {
            // Best-effort — install still proceeds, user just has to relaunch manually.
        }
    }

    private static string Truncate(string text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
            return text;
        return text[..max] + "…";
    }
}
