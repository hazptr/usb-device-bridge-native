using System.Diagnostics;
using System.IO;
using System.Windows;
using Grpc.Core;
using UsbDeviceBridge.App.Settings;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing Windows service control and registry helpers for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private async Task<bool> RestartServiceFromRecoveryPanelAsync()
    {
        var started = TryStartWindowsService();
        if (!started)
            return false;

        await PollUntilServiceReadyAsync();
        return true;
    }

    private async Task PollUntilServiceReadyAsync(
        int timeoutSeconds = 30,
        int intervalMs = 800)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        var elapsed = 0;

        while (!cts.Token.IsCancellationRequested)
        {
            try
            {
                using var call = _client.Device.WatchHeartbeat(
                    new HeartbeatRequest { IntervalMs = (uint)Math.Max(500, intervalMs) },
                    deadline: DateTime.UtcNow.AddMilliseconds(intervalMs - 100),
                    cancellationToken: cts.Token);

                if (await call.ResponseStream.MoveNext(cts.Token))
                {
                    // Received heartbeat — service is up.
                    return;
                }

            }
            catch (RpcException)
            {
                // Not ready yet; keep waiting.
            }
            catch (OperationCanceledException)
            {
                break;
            }

            elapsed += intervalMs;
            _vm.ShowServiceRecoveryPromptForSetup(
                $"Waiting for service to start... ({elapsed / 1000}s)");

            try
            {
                await Task.Delay(intervalMs, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static bool TryStartWindowsService()
    {
#if DEBUG
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", ".."));
        var devScript = Path.Combine(repoRoot, "scripts", "dev.ps1");

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh",
                Arguments = $"-NoExit -File \"{devScript}\" service",
                Verb = "runas",
                UseShellExecute = true,
            });
            return true;
        }
        catch (Exception)
        {
            // UAC cancelled or pwsh not found — retry will surface the error.
            return false;
        }
#else
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "net",
                Arguments = "start UsbDeviceBridge",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            return true;
        }
        catch (Exception)
        {
            // User cancelled UAC or the service name doesn't exist yet.
            return false;
        }
#endif
    }

    private static bool TryConfigureWindowsServiceStartupMode(string mode, out string error)
    {
        error = string.Empty;

        var scStartValue = mode switch
        {
              UsbDeviceBridge.App.Settings.ServiceStartupModes.Automatic => "auto",
              UsbDeviceBridge.App.Settings.ServiceStartupModes.OnDemand  => "demand",
              UsbDeviceBridge.App.Settings.ServiceStartupModes.Manual    => "demand",
            _                             => "auto",
        };

        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"config UsbDeviceBridge start= {scStartValue}",
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });

            if (process is null)
            {
                error = "Could not start sc.exe.";
                return false;
            }

            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                error = $"sc.exe exited with code {process.ExitCode}.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void ReconcileStartWithWindowsRegistry()
    {
        var registryEnabled = _startupRegistry.IsEnabled();

        if (_settings.StartWithWindows && !registryEnabled)
        {
            var exePath = System.Environment.ProcessPath
                ?? System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? string.Empty;
            _startupRegistry.TryEnable(exePath, out _);
        }
        else if (!_settings.StartWithWindows && registryEnabled)
        {
            _startupRegistry.TryDisable(out _);
        }
    }

    private static bool TryScheduleRestartAfterShutdown(out string error)
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            error = "Cannot determine the application executable path.";
            return false;
        }

        try
        {
            var escapedExecutablePath = executablePath.Replace("\"", "\"\"");
            var cmdArgs = $"/c \"ping 127.0.0.1 -n 2 > nul && start \"\" \"{escapedExecutablePath}\"\"";

            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = cmdArgs,
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
