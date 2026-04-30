using System.Diagnostics;
using System.IO;
using System.Windows;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing Windows service control and registry helpers for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    /// <summary>Creates the "service error" retry/start panel shown inside setup steps.</summary>
    private FrameworkElement CreateServiceErrorPanel(string message, bool showStartButton, Func<Task> retry)
    {
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 4, 0, 0) };

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = message,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var buttonRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
        };

        if (showStartButton)
        {
            var startBtn = new System.Windows.Controls.Button
            {
                Content = "Start Service",
                Style = (System.Windows.Style)FindResource("AccentBtn"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 8, 0),
            };

            var retryBtn = new System.Windows.Controls.Button
            {
                Content = "↺  Retry",
                Style = (System.Windows.Style)FindResource("GhostBtn"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };

            startBtn.Click += async (_, _) =>
            {
                startBtn.IsEnabled = false;
                retryBtn.IsEnabled = false;
                startBtn.Content = "Starting…";
                TryStartWindowsService();
                await Task.Delay(3500);
                await retry();
            };
            retryBtn.Click += (_, _) => _ = retry();

            buttonRow.Children.Add(startBtn);
            buttonRow.Children.Add(retryBtn);
        }
        else
        {
            var retryBtn = new System.Windows.Controls.Button
            {
                Content = "↺  Retry",
                Style = (System.Windows.Style)FindResource("GhostBtn"),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            };
            retryBtn.Click += (_, _) => _ = retry();
            buttonRow.Children.Add(retryBtn);
        }

        panel.Children.Add(buttonRow);
        return panel;
    }

    private async Task<bool> RestartServiceFromRecoveryPanelAsync()
    {
        var started = TryStartWindowsService();
        if (!started)
            return false;

        await Task.Delay(3500);
        return true;
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
            var exePath = System.Reflection.Assembly.GetEntryAssembly()?.Location
                ?? System.Environment.ProcessPath
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
