using System.Diagnostics;
using System.Text;
using Grpc.Core;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Services;

/// <summary>
/// Implements the SetupService RPC for prerequisite detection and system setup.
/// Handles usbipd-win and WSL2 installation with elevated privilege execution on service side.
/// </summary>
public sealed class SetupServiceImpl(
    ILogger<SetupServiceImpl> logger
) : SetupService.SetupServiceBase
{
    public override async Task<CheckPrerequisitesResponse> CheckPrerequisites(
        CheckPrerequisitesRequest request,
        ServerCallContext context
    )
    {
        logger.LogInformation("Checking prerequisites...");
        var response = new CheckPrerequisitesResponse();

        var prerequisites = await GetAllPrerequisitesStatusAsync(context.CancellationToken);
        foreach (var prereq in prerequisites)
        {
            response.Prerequisites.Add(prereq);
        }

        // All prerequisites met if no missing or outdated statuses
        response.AllMet = prerequisites.All(p => p.Status == "installed");

        logger.LogInformation(
            "Prerequisite check complete: all_met={AllMet}",
            response.AllMet
        );

        return response;
    }

    public override async Task RunSetup(
        RunSetupRequest request,
        IServerStreamWriter<SetupOutputEvent> responseStream,
        ServerCallContext context
    )
    {
        logger.LogInformation("RunSetup started");
        var ct = context.CancellationToken;

        try
        {
            // Determine which prerequisites need installation
            var prerequisites = await GetMissingPrerequisitesAsync(ct);

            if (prerequisites.Count == 0)
            {
                await responseStream.WriteAsync(new SetupOutputEvent
                {
                    OutputLine = "All prerequisites already installed.",
                    IsError = false,
                    ExitCode = 0,
                });
                logger.LogInformation("All prerequisites already met");
                return;
            }

            // Install each missing prerequisite
            foreach (var prereq in prerequisites)
            {
                await responseStream.WriteAsync(new SetupOutputEvent
                {
                    OutputLine = $"\n>>> Installing {prereq}...",
                    IsError = false,
                    ExitCode = 0,
                });

                if (prereq == "usbipd-win")
                {
                    await RunUsbIpdInstallationAsync(responseStream, ct);
                }
                else if (prereq == "WSL2")
                {
                    await RunWslInstallationAsync(responseStream, ct);
                }
            }

            // Final verification
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "\n>>> Verifying installation...",
                IsError = false,
                ExitCode = 0,
            });

            var allPrereqs = await GetAllPrerequisitesStatusAsync(ct);
            var allMet = allPrereqs.All(p => p.Status == "installed");
            
            if (allMet)
            {
                await responseStream.WriteAsync(new SetupOutputEvent
                {
                    OutputLine = "✓ All prerequisites installed successfully.",
                    IsError = false,
                    ExitCode = 0,
                });
                logger.LogInformation("Setup completed successfully");
            }
            else
            {
                var missing = string.Join(
                    ", ",
                    allPrereqs
                        .Where(p => p.Status != "installed")
                        .Select(p => p.Name)
                );
                await responseStream.WriteAsync(new SetupOutputEvent
                {
                    OutputLine = $"✗ Setup incomplete. Still missing: {missing}",
                    IsError = true,
                    ExitCode = 1,
                });
                logger.LogWarning("Setup completed with missing prerequisites: {Missing}", missing);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Setup failed with exception");
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = $"✗ Setup failed: {ex.Message}",
                IsError = true,
                ExitCode = 1,
            });
        }
    }

    // --- Private helpers ---

    private async Task<List<PrerequisiteStatus>> GetAllPrerequisitesStatusAsync(
        CancellationToken ct
    )
    {
        var statuses = new List<PrerequisiteStatus>();
        statuses.Add(await CheckUsbIpdAsync(ct));
        statuses.Add(await CheckWslAsync(ct));
        return statuses;
    }

    private async Task<PrerequisiteStatus> CheckUsbIpdAsync(CancellationToken ct)
    {
        try
        {
            // Try to find usbipd in common locations
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

            // Try to get version (first non-empty line only)
            var (code, stdout, _) = await RunProcessAsync(usbIpdPath, ["--version"], ct);
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
            // In installed mode the service runs under LocalSystem, which may not be able
            // to enumerate per-user distro registrations. Do not treat "no/failed distro list"
            // as "WSL missing". Instead detect WSL installation from global CLI support.
            var versionResult = await RunProcessAsync("wsl", ["--version"], ct);
            string wslVersion = "unknown";
            if (versionResult.Code == 0 && versionResult.StdOut is { } vOut)
            {
                var firstLine = vOut.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                                    .FirstOrDefault()?.Trim() ?? string.Empty;
                // Strip leading label ("WSL version:", "WSL-Version:", etc.) if present
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

            // Fallback for systems where --version is unsupported but WSL is present.
            var statusResult = await RunProcessAsync("wsl", ["--status"], ct);
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

    private async Task<List<string>> GetMissingPrerequisitesAsync(CancellationToken ct)
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

    private async Task RunUsbIpdInstallationAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        CancellationToken ct
    )
    {
        // First try winget
        var (code, stdout, stderr) = await RunProcessAsync(
            "winget",
            ["install", "--id", "usbipd-win", "-q"],
            ct
        );

        if (stdout != null)
            await WriteOutputLineAsync(responseStream, stdout, false);
        if (stderr != null)
            await WriteOutputLineAsync(responseStream, stderr, code != 0);

        if (code != 0)
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✗ winget installation failed. Please install usbipd-win manually from: https://github.com/dorssel/usbipd-win/releases",
                IsError = true,
                ExitCode = code,
            });
            logger.LogWarning("winget installation of usbipd-win failed with exit code {Code}", code);
        }
        else
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✓ usbipd-win installed successfully",
                IsError = false,
                ExitCode = 0,
            });
        }
    }

    private async Task RunWslInstallationAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        CancellationToken ct
    )
    {
        await responseStream.WriteAsync(new SetupOutputEvent
        {
            OutputLine = "Note: WSL installation requires elevated privileges and may require a system restart.",
            IsError = false,
            ExitCode = 0,
        });

        var (code, stdout, stderr) = await RunProcessAsync("wsl", ["--install"], ct);

        if (stdout != null)
            await WriteOutputLineAsync(responseStream, stdout, false);
        if (stderr != null)
            await WriteOutputLineAsync(responseStream, stderr, code != 0);

        if (code != 0)
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✗ WSL installation may have failed. Please restart your system and try again.",
                IsError = true,
                ExitCode = code,
            });
            logger.LogWarning("WSL installation failed with exit code {Code}", code);
        }
        else
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✓ WSL installed. You may need to restart your system.",
                IsError = false,
                ExitCode = 0,
            });
        }
    }

    private async Task<(int Code, string? StdOut, string? StdErr)> RunProcessAsync(
        string fileName,
        string[] args,
        CancellationToken ct
    )
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, null, "Failed to start process");

            await process.WaitForExitAsync(ct);

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running process {FileName}", fileName);
            return (-1, null, ex.Message);
        }
    }

    private static async Task WriteOutputLineAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        string output,
        bool isError
    )
    {
        // Split multi-line output and write each line separately
        var lines = output.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
                continue;

            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = line,
                IsError = isError,
                ExitCode = 0,
            });
        }
    }
}
