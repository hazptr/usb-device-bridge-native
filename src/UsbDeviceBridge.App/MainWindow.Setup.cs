using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media.Animation;
using Grpc.Core;
using Usbdevicebridge.V1;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Ellipse = System.Windows.Shapes.Ellipse;
using Grid = System.Windows.Controls.Grid;
using Rectangle = System.Windows.Shapes.Rectangle;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing the four-step setup wizard logic for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private int _setupStepIndex;
    private string _setupSelectedTheme = "Dark";
    private bool _setupForceShowingOverlay;
    private List<(string Name, string Status, string Message)>? _setupPrerequisitesStatus;
    private Dictionary<string, bool> _setupSelectedClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _setupCustomSshClients = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _setupInstallCts;
    private bool _setupPrerequisitesVerifiedInstalled;
    private bool _deviceInitializationStarted;

    private Grid SetupStepOnePanel => SetupOverlay.StepOnePanel;

    private Grid SetupStepTwoPanelPrereq => SetupOverlay.StepTwoPanelPrereq;

    private Grid SetupStepThreePanelDistro => SetupOverlay.StepThreePanelDistro;

    private Grid SetupStepFourPanel => SetupOverlay.StepFourPanel;

    private Grid SetupDistroSelectionView => SetupOverlay.DistroSelectionView;

    private Grid SetupDistroLogView => SetupOverlay.DistroLogView;

    private StackPanel SetupDistroCheckboxes => SetupOverlay.DistroCheckboxes;

    private StackPanel SetupPrerequisitesStatus => SetupOverlay.PrerequisitesStatus;

    private TextBox SetupInstallLogText => SetupOverlay.InstallLogText;

    private Button SetupInstallPackagesButton => SetupOverlay.InstallPackagesButton;

    private TextBox SetupAddClientHostText => SetupOverlay.AddClientHostText;

    private Button SetupAddClientButton => SetupOverlay.AddClientButton;

    private Button SetupInstallStopButton => SetupOverlay.InstallStopButton;

    private Button SetupInstallStartOverButton => SetupOverlay.InstallStartOverButton;

    private Button SetupInstallUsbIpdButton => SetupOverlay.InstallUsbIpdButton;

    private Button SetupBackButton => SetupOverlay.BackButton;

    private Button SetupNextButton => SetupOverlay.NextButton;

    private Button SetupDarkCard => SetupOverlay.DarkCard;

    private Button SetupLightCard => SetupOverlay.LightCard;

    private TextBlock SetupDarkLabel => SetupOverlay.DarkLabel;

    private TextBlock SetupLightLabel => SetupOverlay.LightLabel;

    private Rectangle SetupDarkSwatch1 => SetupOverlay.DarkSwatch1;

    private Rectangle SetupDarkSwatch2 => SetupOverlay.DarkSwatch2;

    private Rectangle SetupDarkSwatch3 => SetupOverlay.DarkSwatch3;

    private Rectangle SetupLightSwatch1 => SetupOverlay.LightSwatch1;

    private Rectangle SetupLightSwatch2 => SetupOverlay.LightSwatch2;

    private Rectangle SetupLightSwatch3 => SetupOverlay.LightSwatch3;

    private Ellipse SetupDotOne => SetupOverlay.DotOne;

    private Ellipse SetupDotTwo => SetupOverlay.DotTwo;

    private Ellipse SetupDotThree => SetupOverlay.DotThree;

    private Ellipse SetupDotFour => SetupOverlay.DotFour;

    private CheckBox SetupEnableTray => SetupOverlay.EnableTray;

    private CheckBox SetupStartMinimized => SetupOverlay.StartMinimized;

    private CheckBox SetupAutoRefresh => SetupOverlay.AutoRefresh;

    private CheckBox SetupAutoUpdate => SetupOverlay.AutoUpdate;

    private void InitializeSetupOverlayHandlers()
    {
        // Guard against duplicate subscriptions if initialization is called again.
        SetupDarkCard.Click -= SetupThemeCard_OnClick;
        SetupLightCard.Click -= SetupThemeCard_OnClick;
        SetupBackButton.Click -= SetupBack_OnClick;
        SetupNextButton.Click -= SetupNext_OnClick;
        SetupInstallPackagesButton.Click -= SetupInstallPackages_OnClick;
        SetupAddClientButton.Click -= SetupAddClient_OnClick;
        SetupInstallStopButton.Click -= SetupInstallStop_OnClick;
        SetupInstallStartOverButton.Click -= SetupInstallStartOver_OnClick;
        SetupInstallUsbIpdButton.Click -= SetupInstallUsbIpd_OnClick;

        SetupDarkCard.Click += SetupThemeCard_OnClick;
        SetupLightCard.Click += SetupThemeCard_OnClick;
        SetupBackButton.Click += SetupBack_OnClick;
        SetupNextButton.Click += SetupNext_OnClick;
        SetupInstallPackagesButton.Click += SetupInstallPackages_OnClick;
        SetupAddClientButton.Click += SetupAddClient_OnClick;
        SetupInstallStopButton.Click += SetupInstallStop_OnClick;
        SetupInstallStartOverButton.Click += SetupInstallStartOver_OnClick;
        SetupInstallUsbIpdButton.Click += SetupInstallUsbIpd_OnClick;
    }

    private void OnServiceReconnectedDuringSetup()
    {
        // If setup is on the prerequisites step and the recovery panel is showing,
        // the service just came back — auto-retry the check without requiring user action.
        if (_setupStepIndex == 1 && SetupOverlay.Visibility == Visibility.Visible)
            _ = PopulatePrerequisitesStatusAsync();
    }

    private void ShowSetupOverlay()
    {
        _setupStepIndex = 0;
        _setupSelectedTheme = "Dark";
        _setupPrerequisitesVerifiedInstalled = false;
        _setupCustomSshClients.Clear();
        foreach (var client in _settings.AdditionalSshClients)
            _setupCustomSshClients.Add(client);
        SetupAddClientHostText.Text = string.Empty;
        _vm.StatusText = "Setup in progress";
        UpdateSetupStepUi();
        ApplySetupCardPreviews();
        ApplySetupThemeCardSelection();
        SetupOverlay.Visibility = Visibility.Visible;
    }

    private void SetupThemeCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button card)
            return;

        _setupSelectedTheme = card.Tag?.ToString() == "Light" ? "Light" : "Dark";
        Theming.ThemeManager.ApplyTheme(_setupSelectedTheme);
        ApplySetupThemeCardSelection();
    }

    private void SetupBack_OnClick(object sender, RoutedEventArgs e)
    {
        if (_setupStepIndex > 0)
        {
            if (_setupStepIndex == 2 && SetupDistroLogView.Visibility == Visibility.Visible)
            {
                _setupInstallCts?.Cancel();
                SetupInstallLogText.Text = string.Empty;
                SetupDistroLogView.Visibility = Visibility.Collapsed;
                SetupDistroSelectionView.Visibility = Visibility.Visible;
                SetupNextButton.IsEnabled = true;
                return;
            }
            _setupStepIndex--;
            UpdateSetupStepUi();
        }
    }

    private async void SetupNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_setupStepIndex == 0)
        {
            _setupStepIndex = 1;
            UpdateSetupStepUi();
            _ = PopulatePrerequisitesStatusAsync();
            return;
        }

        if (_setupStepIndex == 1)
        {
            _setupStepIndex = 2;
            UpdateSetupStepUi();
            _ = PopulateClientCheckboxesAsync();
            return;
        }

        if (_setupStepIndex == 2)
        {
            SetupInstallLogText.Text = string.Empty;
            SetupInstallStartOverButton.Visibility = Visibility.Collapsed;
            SetupDistroLogView.Visibility = Visibility.Collapsed;
            SetupDistroSelectionView.Visibility = Visibility.Visible;
            _setupStepIndex = 3;
            UpdateSetupStepUi();
            return;
        }

        // Finish — persist choices, kick off device load, then fade the overlay away.
        _settings.SetupCompleted = true;
        _settings.Theme = Theming.ThemeManager.NormalizeTheme(_setupSelectedTheme);
        _settings.MinimizeToTray = SetupEnableTray.IsChecked == true;
        _settings.StartMinimized = SetupStartMinimized.IsChecked == true;
        _settings.AutoRefreshEnabled = SetupAutoRefresh.IsChecked == true;
        _settings.AutoUpdateEnabled = SetupAutoUpdate.IsChecked == true;
        _settings.AdditionalSshClients = _setupCustomSshClients
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _vm.IsAutoRefresh = _settings.AutoRefreshEnabled;
        _settingsService.Save(_settings);

        await DismissSetupOverlayAsync();
        await TryInitializeDevicesAfterPrerequisitesAsync(verifyWithService: false);
    }

    private async Task<bool> QueryPrerequisitesInstalledAsync()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var response = await _client.Setup.CheckPrerequisitesAsync(
                new CheckPrerequisitesRequest(),
                deadline: deadline);

            _setupPrerequisitesStatus = response.Prerequisites
                .Select(p => (p.Name, p.Status, p.Message))
                .ToList();

            _setupPrerequisitesVerifiedInstalled = response.Prerequisites.All(
                p => string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase));

            return _setupPrerequisitesVerifiedInstalled;
        }
        catch (RpcException)
        {
            _setupPrerequisitesVerifiedInstalled = false;
            return false;
        }
    }

    private async Task TryInitializeDevicesAfterPrerequisitesAsync(bool verifyWithService)
    {
        if (_deviceInitializationStarted)
            return;

        if (SetupOverlay.Visibility == Visibility.Visible)
            return;

        var prerequisitesInstalled = _setupPrerequisitesVerifiedInstalled;
        if (!prerequisitesInstalled && verifyWithService)
            prerequisitesInstalled = await QueryPrerequisitesInstalledAsync();

        if (!prerequisitesInstalled)
        {
            _vm.StatusText = "Setup required: install prerequisites";
            if (SetupOverlay.Visibility != Visibility.Visible)
                ShowSetupOverlay();
            return;
        }

        _deviceInitializationStarted = true;
        await _vm.InitializeAsync();
    }

    private readonly record struct SetupClientInstallTarget(string Key, AttachTargetType Type, string Name, string Label);

    private List<SetupClientInstallTarget> _setupAvailableClients = [];

    private async Task PopulateClientCheckboxesAsync()
    {
        SetupDistroCheckboxes.Children.Clear();

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Loading available clients...",
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        SetupDistroCheckboxes.Children.Add(loadingText);

        _setupAvailableClients = await QuerySetupClientsAsync();
        var previousSelections = new Dictionary<string, bool>(_setupSelectedClients, StringComparer.OrdinalIgnoreCase);
        _setupSelectedClients.Clear();

        SetupDistroCheckboxes.Children.Clear();

        if (_setupAvailableClients.Count == 0)
        {
            SetupDistroCheckboxes.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No clients detected. Add an SSH client above, or configure clients later from Settings.",
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var client in _setupAvailableClients)
        {
            var isSelected = previousSelections.TryGetValue(client.Key, out var selected) && selected;
            _setupSelectedClients[client.Key] = isSelected;

            var checkboxPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var checkbox = new System.Windows.Controls.CheckBox
            {
                Style = (System.Windows.Style)FindResource("ModernCheckBox"),
                IsChecked = isSelected,
                Margin = new Thickness(0, 0, 12, 0),
                Tag = client.Key
            };

            checkbox.Checked += (_, _) =>
            {
                if (checkbox.Tag is string key)
                    _setupSelectedClients[key] = true;
            };
            checkbox.Unchecked += (_, _) =>
            {
                if (checkbox.Tag is string key)
                    _setupSelectedClients[key] = false;
            };

            var nameText = new System.Windows.Controls.TextBlock
            {
                Text = client.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            };

            checkboxPanel.Children.Add(checkbox);
            checkboxPanel.Children.Add(nameText);
            SetupDistroCheckboxes.Children.Add(checkboxPanel);
        }
    }

    private async Task<List<SetupClientInstallTarget>> QuerySetupClientsAsync()
    {
        var clients = new List<SetupClientInstallTarget>();

        try
        {
            var distros = await _wslUserSpaceInterop.QueryDistrosAsync();
            foreach (var distro in distros.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var versionSuffix = distro.Version.Length > 0 ? $" (WSL{distro.Version})" : string.Empty;
                var runningSuffix = distro.IsRunning ? string.Empty : " [not running]";
                var label = $"WSL | {distro.Name}{versionSuffix}{runningSuffix}";
                clients.Add(new SetupClientInstallTarget(
                    KeyForSetupClient(AttachTargetType.Wsl, distro.Name),
                    AttachTargetType.Wsl,
                    distro.Name,
                    label));
            }
        }
        catch
        {
            // Keep partial list if WSL query fails.
        }

        var sshClients = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var host in _sshConfigParser.GetHostAliases())
                sshClients.Add(host);
        }
        catch
        {
            // Keep only custom hosts if SSH config parsing fails.
        }

        foreach (var custom in _setupCustomSshClients)
            sshClients.Add(custom);

        foreach (var host in sshClients.OrderBy(h => h, StringComparer.OrdinalIgnoreCase))
        {
            clients.Add(new SetupClientInstallTarget(
                KeyForSetupClient(AttachTargetType.Ssh, host),
                AttachTargetType.Ssh,
                host,
                $"SSH | {host}"));
        }

        return clients;
    }

    private static string KeyForSetupClient(AttachTargetType type, string name)
        => $"{type}:{name.Trim()}";

    private async void SetupAddClient_OnClick(object sender, RoutedEventArgs e)
    {
        var host = (SetupAddClientHostText.Text ?? string.Empty).Trim();
        if (host.Length == 0)
            return;

        _setupCustomSshClients.Add(host);
        SetupAddClientHostText.Text = string.Empty;
        await PopulateClientCheckboxesAsync();
    }

    private void SetupInstallPackages_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = _setupAvailableClients
            .Where(client => _setupSelectedClients.TryGetValue(client.Key, out var isSelected) && isSelected)
            .ToList();

        if (selected.Count == 0)
        {
            _setupStepIndex = 3;
            UpdateSetupStepUi();
            return;
        }

        _ = RunClientInstallAsync(selected);
    }

    private async Task RunClientInstallAsync(IReadOnlyList<SetupClientInstallTarget> clients)
    {
        SetupDistroSelectionView.Visibility = Visibility.Collapsed;
        SetupDistroLogView.Visibility = Visibility.Visible;
        SetupNextButton.IsEnabled = false;
        SetupBackButton.IsEnabled = false;
        SetupInstallLogText.Text = string.Empty;
        SetupInstallStopButton.IsEnabled = true;
        SetupInstallStopButton.Visibility = Visibility.Visible;
        SetupInstallStartOverButton.Visibility = Visibility.Collapsed;

        _setupInstallCts = new CancellationTokenSource();
        var ct = _setupInstallCts.Token;

        bool success = false;
        bool hadErrors = false;
        try
        {
            var packages = new[] { "usbutils", "linux-tools-generic", "hwdata" };
            foreach (var client in clients)
            {
                AppendInstallLog($"\n>>> Configuring {client.Label}...", false);

                int updateExitCode;
                int installExitCode;
                var packageList = string.Join(" ", packages);
                if (client.Type == AttachTargetType.Wsl)
                {
                    AppendInstallLog("  $ apt-get update", false);
                    updateExitCode = await _wslUserSpaceInterop.RunCommandInDistroStreamingAsync(
                        client.Name,
                        "apt-get update",
                        (line, isError) =>
                        {
                            AppendInstallLog(line, isError);
                            return Task.CompletedTask;
                        },
                        ct,
                        user: "root"
                    );

                    if (updateExitCode != 0)
                        AppendInstallLog($"  ! apt-get update exited with code {updateExitCode}", false);

                    AppendInstallLog($"\n  $ apt-get install -y {packageList}", false);
                    installExitCode = await _wslUserSpaceInterop.RunCommandInDistroStreamingAsync(
                        client.Name,
                        $"apt-get install -y {packageList}",
                        (line, isError) =>
                        {
                            AppendInstallLog(line, isError);
                            return Task.CompletedTask;
                        },
                        ct,
                        user: "root"
                    );
                }
                else
                {
                    var updateCommand = "sh -lc \"sudo -n apt-get update || apt-get update\"";
                    var installCommand = $"sh -lc \"sudo -n apt-get install -y {packageList} || apt-get install -y {packageList}\"";

                    AppendInstallLog($"  $ ssh {client.Name} {updateCommand}", false);
                    updateExitCode = await RunSshCommandStreamingAsync(
                        client.Name,
                        updateCommand,
                        (line, isError) =>
                        {
                            AppendInstallLog(line, isError);
                            return Task.CompletedTask;
                        },
                        ct);

                    if (updateExitCode != 0)
                        AppendInstallLog($"  ! apt-get update exited with code {updateExitCode}", false);

                    AppendInstallLog($"\n  $ ssh {client.Name} {installCommand}", false);
                    installExitCode = await RunSshCommandStreamingAsync(
                        client.Name,
                        installCommand,
                        (line, isError) =>
                        {
                            AppendInstallLog(line, isError);
                            return Task.CompletedTask;
                        },
                        ct);
                }

                if (installExitCode != 0)
                {
                    hadErrors = true;
                    AppendInstallLog($"\n  x Package installation failed (exit code {installExitCode})", true);
                }
                else
                {
                    AppendInstallLog($"\n  ok Packages installed: {packageList}", false);
                }
            }

            success = !hadErrors;
            AppendInstallLog(
                success
                    ? "\nok Selected clients configured successfully."
                    : "\nx Configuration finished with errors. Review the output above.",
                isError: !success
            );
        }
        catch (OperationCanceledException)
        {
            AppendInstallLog("\n— Installation stopped —", false);
        }
        catch (Exception ex)
        {
            AppendInstallLog($"\n✗ Error: {ex.Message}", true);
        }
        finally
        {
            _setupInstallCts?.Dispose();
            _setupInstallCts = null;
        }

        SetupInstallStopButton.IsEnabled = false;
        SetupInstallStopButton.Visibility = Visibility.Collapsed;
        SetupInstallStartOverButton.Visibility = Visibility.Visible;
        SetupNextButton.IsEnabled = true;
        SetupBackButton.IsEnabled = true;

        if (success)
            SetupNextButton.Content = "Next →";
    }

    private static async Task<int> RunSshCommandStreamingAsync(
        string host,
        string command,
        Func<string, bool, Task> onLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi);
        if (process is null)
            return -1;

        using var registration = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore cancellation race during process shutdown
            }
        });

        var outputTask = ReadLinesAsync(process.StandardOutput, onLine, isError: false, ct);
        var errorTask = ReadLinesAsync(process.StandardError, onLine, isError: true, ct);

        await process.WaitForExitAsync(ct);
        await Task.WhenAll(outputTask, errorTask);
        return process.ExitCode;
    }

    private static async Task ReadLinesAsync(
        StreamReader reader,
        Func<string, bool, Task> onLine,
        bool isError,
        CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(ct);
            if (line is null)
                break;

            await onLine(line, isError);
        }
    }

    private void AppendInstallLog(string line, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendInstallLog(line, isError));
            return;
        }

        SetupInstallLogText.AppendText(line.Replace("\0", string.Empty) + "\n");
        SetupInstallLogText.ScrollToEnd();
    }

    private void SetupInstallStop_OnClick(object sender, RoutedEventArgs e)
    {
        _setupInstallCts?.Cancel();
    }

    private void SetupInstallStartOver_OnClick(object sender, RoutedEventArgs e)
    {
        SetupInstallLogText.Text = string.Empty;
        SetupInstallStartOverButton.Visibility = Visibility.Collapsed;
        SetupDistroLogView.Visibility = Visibility.Collapsed;
        SetupDistroSelectionView.Visibility = Visibility.Visible;
        SetupNextButton.IsEnabled = true;
        SetupBackButton.IsEnabled = true;
    }

    private async void SetupInstallUsbIpd_OnClick(object sender, RoutedEventArgs e)
    {
        var originalStatus = _vm.StatusText;
        SetupInstallUsbIpdButton.IsEnabled = false;
        SetupInstallUsbIpdButton.Content = "Installing...";
        _vm.StatusText = "Installing usbipd-win via winget";

        try
        {
            var (success, message) = await InstallUsbIpdViaWingetAsync();
            ShowThemedNoticeDialog(
                success ? "usbipd-win install" : "usbipd-win install failed",
                message,
                "OK");

            await PopulatePrerequisitesStatusAsync();
        }
        finally
        {
            SetupInstallUsbIpdButton.Content = "Install usbipd-win (winget)";
            SetupInstallUsbIpdButton.IsEnabled = true;
            _vm.StatusText = originalStatus;
        }
    }

    private static async Task<(bool Success, string Message)> InstallUsbIpdViaWingetAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "winget",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add("install");
            psi.ArgumentList.Add("--id");
            psi.ArgumentList.Add("dorssel.usbipd-win");
            psi.ArgumentList.Add("--exact");
            psi.ArgumentList.Add("--accept-package-agreements");
            psi.ArgumentList.Add("--accept-source-agreements");

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "Failed to start winget.");
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (process.ExitCode == 0)
            {
                return (true, "usbipd-win installation completed successfully.");
            }

            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            if (string.IsNullOrWhiteSpace(details))
                details = "winget returned a non-zero exit code.";

            var tail = string.Join(
                Environment.NewLine,
                details
                    .Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries)
                    .TakeLast(8));

            return (false, $"winget install failed (exit code {process.ExitCode}).{Environment.NewLine}{Environment.NewLine}{tail}");
        }
        catch (Exception ex)
        {
            return (false, $"Unable to run winget install. {ex.Message}");
        }
    }

    private async Task PopulatePrerequisitesStatusAsync()
    {
        SetupPrerequisitesStatus.Children.Clear();
        SetupNextButton.IsEnabled = false;

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Checking prerequisites...",
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        SetupPrerequisitesStatus.Children.Add(loadingText);

        CheckPrerequisitesResponse response;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            response = await _client.Setup.CheckPrerequisitesAsync(
                new CheckPrerequisitesRequest(),
                deadline: deadline);

            _setupPrerequisitesStatus = response.Prerequisites
                .Select(p => (p.Name, p.Status, p.Message))
                .ToList();
            _setupPrerequisitesVerifiedInstalled = response.Prerequisites.All(
                p => string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase));
        }
        catch (RpcException ex)
        {
            var isDown = ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;
            _setupPrerequisitesVerifiedInstalled = false;
            SetupPrerequisitesStatus.Children.Clear();
            if (isDown)
            {
                _vm.ShowServiceRecoveryPromptForSetup(
                    "Setup can't continue until the background service is running. Start Service to continue prerequisite checks.");
            }
            else
            {
                _vm.ShowServiceRecoveryPromptForSetup($"Service error: {ex.Status.Detail}");
            }
            SetupNextButton.IsEnabled = true;
            return;
        }

        _vm.HideServiceRecoveryPromptForSetup();
        SetupPrerequisitesStatus.Children.Clear();

        var usbipdMissing = response.Prerequisites.Any(p =>
            string.Equals(p.Name, "usbipd-win", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase));

        SetupInstallUsbIpdButton.Visibility = usbipdMissing
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (var prereq in response.Prerequisites)
        {
            var itemStack = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var headerStack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var isInstalled = prereq.Status == "installed";
            var statusSymbol = new System.Windows.Controls.TextBlock
            {
                Text = isInstalled ? "✓ " : "✗ ",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = isInstalled
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.OrangeRed)
            };

            var nameText = new System.Windows.Controls.TextBlock
            {
                Text = prereq.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            };

            headerStack.Children.Add(statusSymbol);
            headerStack.Children.Add(nameText);

            var messageText = new System.Windows.Controls.TextBlock
            {
                Text = prereq.Message.Length > 0 ? prereq.Message : prereq.Status,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            };

            itemStack.Children.Add(headerStack);
            itemStack.Children.Add(messageText);
            SetupPrerequisitesStatus.Children.Add(itemStack);
        }

        SetupNextButton.IsEnabled = true;
    }

    private void UpdateSetupStepUi()
    {
        SetupStepOnePanel.Visibility = _setupStepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupStepTwoPanelPrereq.Visibility = _setupStepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SetupStepThreePanelDistro.Visibility = _setupStepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetupStepFourPanel.Visibility = _setupStepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        SetupBackButton.IsEnabled = _setupStepIndex > 0;
        SetupNextButton.Content = _setupStepIndex == 3 ? "Finish"
                                : _setupStepIndex == 2 ? "Skip"
                                : "Next";

        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");

        SetupDotOne.Fill   = _setupStepIndex == 0 ? accentBrush : borderBrush;
        SetupDotTwo.Fill   = _setupStepIndex == 1 ? accentBrush : borderBrush;
        SetupDotThree.Fill = _setupStepIndex == 2 ? accentBrush : borderBrush;
        SetupDotFour.Fill  = _setupStepIndex == 3 ? accentBrush : borderBrush;
    }

    private void ApplySetupCardPreviews()
    {
        ApplyCardPreview("Dark",  SetupDarkCard,  SetupDarkLabel,  SetupDarkSwatch1,  SetupDarkSwatch2,  SetupDarkSwatch3);
        ApplyCardPreview("Light", SetupLightCard, SetupLightLabel, SetupLightSwatch1, SetupLightSwatch2, SetupLightSwatch3);
    }

    private static void ApplyCardPreview(
        string themeName,
        System.Windows.Controls.Button card,
        System.Windows.Controls.TextBlock label,
        System.Windows.Shapes.Rectangle swatch1,
        System.Windows.Shapes.Rectangle swatch2,
        System.Windows.Shapes.Rectangle swatch3)
    {
        var p = Theming.ThemeManager.GetPreview(themeName);
        card.Background  = new System.Windows.Media.SolidColorBrush(p.CardBackground);
        label.Foreground = new System.Windows.Media.SolidColorBrush(p.TextPrimary);
        swatch1.Fill     = new System.Windows.Media.SolidColorBrush(p.TextMuted);
        swatch2.Fill     = new System.Windows.Media.SolidColorBrush(p.Accent);
        swatch3.Fill     = new System.Windows.Media.SolidColorBrush(p.Success);
    }

    private void ApplySetupThemeCardSelection()
    {
        var selected = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var unselected = (System.Windows.Media.Brush)FindResource("BorderBrush");

        SetupDarkCard.BorderBrush = _setupSelectedTheme == "Dark" ? selected : unselected;
        SetupDarkCard.BorderThickness = _setupSelectedTheme == "Dark" ? new Thickness(2) : new Thickness(1);
        SetupLightCard.BorderBrush = _setupSelectedTheme == "Light" ? selected : unselected;
        SetupLightCard.BorderThickness = _setupSelectedTheme == "Light" ? new Thickness(2) : new Thickness(1);
    }

    private Task DismissSetupOverlayAsync()
    {
        if (SetupOverlay.Visibility != Visibility.Visible)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            SetupOverlay.Opacity = 1.0;
            completion.TrySetResult();
        };
        SetupOverlay.BeginAnimation(OpacityProperty, fade);
        return completion.Task;
    }
}
