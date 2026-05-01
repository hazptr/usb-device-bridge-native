using UsbDeviceBridge.Service.Interop;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Setup;

/// <summary>
/// Checks whether prerequisites (usbipd-win and WSL2) are installed.
/// </summary>
public interface IPrerequisiteChecker
{
    Task<List<PrerequisiteStatus>> GetAllPrerequisitesStatusAsync(CancellationToken ct);
    Task<List<string>> GetMissingPrerequisitesAsync(CancellationToken ct);
}

/// <summary>
/// Concrete implementation of <see cref="IPrerequisiteChecker"/>.
/// </summary>
internal sealed class PrerequisiteChecker(
    ILogger<PrerequisiteChecker> logger,
    ISetupProcessRunner processRunner
) : IPrerequisiteChecker
{
    public async Task<List<PrerequisiteStatus>> GetAllPrerequisitesStatusAsync(CancellationToken ct)
    {
        return
        [
            await CheckUsbIpdAsync(ct),
            await CheckWslAsync(ct),
        ];
    }

    public async Task<List<string>> GetMissingPrerequisitesAsync(CancellationToken ct)
    {
        var usbIpdStatus = await CheckUsbIpdAsync(ct);
        var wslStatus = await CheckWslAsync(ct);

        var missing = new List<string>();
        if (usbIpdStatus.Status != "installed")
            missing.Add("usbipd-win");
        if (wslStatus.Status != "installed")
            missing.Add("WSL2");

        return missing;
    }

    private async Task<PrerequisiteStatus> CheckUsbIpdAsync(CancellationToken ct)
    {
        try
        {
            var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            var pfx86 =
                Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                ?? @"C:\Program Files (x86)";

            string? usbIpdPath = null;
            foreach (var dir in new[] { pf, pfx86 })
            {
                var candidate = Path.Combine(dir, "usbipd-win", "usbipd.exe");
                if (File.Exists(candidate))
                {
                    usbIpdPath = candidate;
                    break;
                }
            }

            if (usbIpdPath == null)
            {
                return new PrerequisiteStatus
                {
                    Name = "usbipd-win",
                    Status = "missing",
                    Version = "",
                    Message = "usbipd-win not found. Install from: winget install usbipd-win",
                };
            }

            var (code, stdout, _) = await processRunner.RunProcessAsync(usbIpdPath, ["--version"], ct);
            var version = code == 0
                ? stdout?.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "unknown"
                : "unknown";

            return new PrerequisiteStatus
            {
                Name = "usbipd-win",
                Status = "installed",
                Version = version,
                Message = $"usbipd-win {version}",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking usbipd-win");
            return new PrerequisiteStatus
            {
                Name = "usbipd-win",
                Status = "missing",
                Version = "",
                Message = $"Error checking usbipd-win: {ex.Message}",
            };
        }
    }

    private async Task<PrerequisiteStatus> CheckWslAsync(CancellationToken ct)
    {
        try
        {
            var versionResult = await processRunner.RunProcessAsync("wsl", ["--version"], ct);
            string wslVersion = "unknown";
            if (versionResult.Code == 0 && versionResult.StdOut is { } vOut)
            {
                var firstLine = vOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault()?.Trim() ?? string.Empty;
                var colonIdx = firstLine.IndexOf(':');
                wslVersion = colonIdx >= 0
                    ? firstLine[(colonIdx + 1)..].Trim()
                    : firstLine;
                if (string.IsNullOrEmpty(wslVersion)) wslVersion = "unknown";

                return new PrerequisiteStatus
                {
                    Name = "WSL2",
                    Status = "installed",
                    Version = wslVersion,
                    Message = $"WSL2 {wslVersion}",
                };
            }

            var statusResult = await processRunner.RunProcessAsync("wsl", ["--status"], ct);
            if (statusResult.Code == 0)
            {
                return new PrerequisiteStatus
                {
                    Name = "WSL2",
                    Status = "installed",
                    Version = "unknown",
                    Message = "WSL2 installed",
                };
            }

            return new PrerequisiteStatus
            {
                Name = "WSL2",
                Status = "missing",
                Version = "",
                Message = "WSL2 not found. Install with: wsl --install",
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking WSL2");
            return new PrerequisiteStatus
            {
                Name = "WSL2",
                Status = "missing",
                Version = "",
                Message = $"Error checking WSL2: {ex.Message}",
            };
        }
    }
}
