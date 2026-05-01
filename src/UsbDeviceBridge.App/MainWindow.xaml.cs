using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Grpc.Core;
using UsbDeviceBridge.App.Services;
using UsbDeviceBridge.App.Settings;
using UsbDeviceBridge.App.Shell;
using UsbDeviceBridge.App.Theming;
using UsbDeviceBridge.App.ViewModels;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly MainViewModel _vm;
    private readonly AppSettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly TrayIconManager _tray;
    private readonly BridgeServiceClient _client;
    private readonly WslUserSpaceInterop _wslUserSpaceInterop;
    private readonly SettingsResetService _settingsResetService;
    private readonly WindowsStartupRegistryService _startupRegistry;
    private readonly LocalDeviceManager _deviceManager;
    private readonly bool _isFirstRun;
    private bool _exitingFromTray;
    private bool _isRefreshingVersionInfo;
    private string _backendVersion = "Loading...";
    private string _wslVersion = "Loading...";
    private string _usbIpdVersion = "Loading...";

    public event PropertyChangedEventHandler? PropertyChanged;

    public IReadOnlyList<string> AvailableThemes => ThemeManager.AvailableThemes;

    public IReadOnlyList<string> SortOrders { get; } = ["State then name", "Name"];

    public IReadOnlyList<string> ServiceStartupModes { get; } = UsbDeviceBridge.App.Settings.ServiceStartupModes.All;

    public IReadOnlyList<string> FirewallFixPolicyOptions { get; } =
    [
        UsbDeviceBridge.App.Settings.FirewallFixPolicies.Ask,
        UsbDeviceBridge.App.Settings.FirewallFixPolicies.Always,
        UsbDeviceBridge.App.Settings.FirewallFixPolicies.Never,
    ];

    public string FrontendVersion { get; } =
        (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? (Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly())
            .GetName().Version?.ToString()
        ?? "Unknown";

    public string BackendVersion
    {
        get => _backendVersion;
        private set
        {
            if (string.Equals(_backendVersion, value, StringComparison.Ordinal))
            {
                return;
            }

            _backendVersion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackendVersion)));
        }
    }

    public string WslVersion
    {
        get => _wslVersion;
        private set
        {
            if (string.Equals(_wslVersion, value, StringComparison.Ordinal))
            {
                return;
            }

            _wslVersion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WslVersion)));
        }
    }

    public string UsbIpdVersion
    {
        get => _usbIpdVersion;
        private set
        {
            if (string.Equals(_usbIpdVersion, value, StringComparison.Ordinal))
            {
                return;
            }

            _usbIpdVersion = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UsbIpdVersion)));
        }
    }

    public string SelectedTheme
    {
        get => _settings.Theme;
        set
        {
            var normalized = ThemeManager.NormalizeTheme(value);
            if (string.Equals(_settings.Theme, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.Theme = normalized;
            ThemeManager.ApplyTheme(_settings.Theme);
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedTheme)));
        }
    }

    public bool MinimizeToTrayEnabled
    {
        get => _settings.MinimizeToTray;
        set
        {
            if (_settings.MinimizeToTray == value)
            {
                return;
            }

            _settings.MinimizeToTray = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MinimizeToTrayEnabled)));
            UpdateTrayIconState();
        }
    }

    public bool StartMinimizedEnabled
    {
        get => _settings.StartMinimized;
        set
        {
            if (_settings.StartMinimized == value)
            {
                return;
            }

            _settings.StartMinimized = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartMinimizedEnabled)));
        }
    }

    public bool AutoRefreshEnabled
    {
        get => _vm.IsAutoRefresh;
        set
        {
            if (_vm.IsAutoRefresh == value)
            {
                return;
            }

            _vm.IsAutoRefresh = value;
            _settings.AutoRefreshEnabled = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoRefreshEnabled)));
        }
    }

    public bool AutoUpdateEnabled
    {
        get => _settings.AutoUpdateEnabled;
        set
        {
            if (_settings.AutoUpdateEnabled == value)
            {
                return;
            }

            _settings.AutoUpdateEnabled = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoUpdateEnabled)));
        }
    }

    public string SortOrderSelected
    {
        get => _settings.SortOrder;
        set
        {
            if (string.Equals(_settings.SortOrder, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.SortOrder = value;
            _vm.SetSortOrder(value);
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SortOrderSelected)));
        }
    }

    public string ServiceStartupModeSelected
    {
        get => _settings.ServiceStartupMode;
        set
        {
            var normalized = UsbDeviceBridge.App.Settings.ServiceStartupModes.Normalize(value);
            if (string.Equals(_settings.ServiceStartupMode, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!TryConfigureWindowsServiceStartupMode(normalized, out var error))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServiceStartupModeSelected)));
                System.Windows.MessageBox.Show(
                    $"Failed to set service startup mode. {error}",
                    "Service startup mode",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            _settings.ServiceStartupMode = normalized;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServiceStartupModeSelected)));
        }
    }

    public string FirewallFixPolicySelected
    {
        get => _settings.FirewallFixPolicy;
        set
        {
            var normalized = FirewallFixPolicies.Normalize(value);
            if (string.Equals(_settings.FirewallFixPolicy, normalized, StringComparison.OrdinalIgnoreCase))
                return;

            _settings.FirewallFixPolicy = normalized;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirewallFixPolicySelected)));
        }
    }

    public bool WindowsNotificationsEnabled
    {
        get => _settings.WindowsNotificationsEnabled;
        set
        {
            if (_settings.WindowsNotificationsEnabled == value)
                return;

            _settings.WindowsNotificationsEnabled = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowsNotificationsEnabled)));
        }
    }

    public bool DetachOnExitEnabled
    {
        get => _settings.DetachOnExit;
        set
        {
            if (_settings.DetachOnExit == value)
                return;

            _settings.DetachOnExit = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DetachOnExitEnabled)));
        }
    }

    public bool StartWithWindowsEnabled
    {
        get => _settings.StartWithWindows;
        set
        {
            if (_settings.StartWithWindows == value)
            {
                return;
            }

            _settings.StartWithWindows = value;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StartWithWindowsEnabled)));
            ReconcileStartWithWindowsRegistry();
        }
    }

    public MainWindow(AppSettingsService settingsService, AppSettings settings, bool isFirstRun = false, bool forceSetup = false)
    {
        _isFirstRun = isFirstRun;
        _setupForceShowingOverlay = forceSetup && !isFirstRun;
        InitializeComponent();
        InitializeSetupOverlayHandlers();

        _settingsService = settingsService;
        _settings = settings;
        _tray = new TrayIconManager();
        _tray.ShowRequested += ToggleWindowFromTray;
        _tray.SettingsRequested += OpenSettingsFromTray;
        _tray.ExitRequested += ExitFromTray;

        var serviceAddress =
            Environment.GetEnvironmentVariable("USB_DEVICE_BRIDGE_SERVICE_URL")
            ?? "http://127.0.0.1:5205";

        _client = new BridgeServiceClient(serviceAddress);
        _wslUserSpaceInterop = new WslUserSpaceInterop();

        var usbIpdClient = new UsbIpdClient();
        _deviceManager = new LocalDeviceManager(usbIpdClient);
        var rememberedStore = new AppRememberedDeviceStore();
        var autoAttachManager = new LocalAutoAttachManager(_client, _deviceManager, rememberedStore, RequestForceRetryAsync);

        _settingsResetService = new SettingsResetService(
            rememberedStore, _deviceManager, _client, _settingsService);
        _startupRegistry = new WindowsStartupRegistryService();
        _vm = new MainViewModel(
            _client,
            _deviceManager,
            rememberedStore,
            autoAttachManager,
            RestartServiceFromRecoveryPanelAsync,
            () => _settings.FirewallFixPolicy,
            RequestFirewallConsentAsync,
            RequestForceRetryAsync,
            () => IsActive && Visibility == Visibility.Visible && WindowState != WindowState.Minimized,
            async (title, message, severity) =>
            {
                if (_settings.WindowsNotificationsEnabled)
                {
                    _tray.ShowOsNotification(title, message, severity);
                    await Task.CompletedTask;
                }
            });
        _vm.IsAutoRefresh = _settings.AutoRefreshEnabled;
        _vm.SetSortOrder(_settings.SortOrder);
        _vm.ToastShown += () => ToastMenuPanel.AnimateIn();
        _vm.ToastDismissRequested += () => ToastMenuPanel.AnimateOut();
        _vm.ServiceReconnected += OnServiceReconnectedDuringSetup;
        _vm.Devices.CollectionChanged += Devices_CollectionChanged;

        NotificationMenuPanel.CloseRequested += CloseNotificationMenu_OnClick;
        NotificationMenuPanel.FilterRequested += FilterButton_OnClick;
        NotificationMenuPanel.DismissRequested += DismissNotification_OnClick;
        NotificationMenuPanel.CopyRequested += CopyNotification_OnClick;
        NotificationMenuPanel.MarkAllAsReadRequested += MarkAllAsRead_OnClick;
        NotificationMenuPanel.ClearAllRequested += ClearAllNotifications_OnClick;

        ToastMenuPanel.DismissAnimationCompleted += (_, _) => _vm.ClearFeedback();

        _vm.NotificationService.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Services.NotificationService.UnreadCount))
            {
                Dispatcher.Invoke(UpdateNotificationBadgeVisibility);
            }
        };

        _vm.NotificationService.NotificationAdded += (_, _) =>
        {
            if (NotificationMenuPanel.Visibility == Visibility.Visible)
            {
                Dispatcher.Invoke(() => ApplyNotificationFilter(_currentNotificationFilter));
            }
        };

        SettingsOverlay.CloseRequested += CloseSettingsButton_OnClick;
        SettingsOverlay.SearchTextChanged += SettingsSearchBox_OnTextChanged;
        SettingsOverlay.CheckUsbIpRequested += CheckUsbIp_OnClick;
        SettingsOverlay.OpenUsbIpDocsRequested += OpenUsbIpDocs_OnClick;
        SettingsOverlay.ResetSetupRequested += ResetSetup_OnClick;
        SettingsOverlay.CopyVersionInfoRequested += CopyVersionInfo_OnClick;

        DataContext = _vm;
        SettingsOverlay.DataContext = this;
        ShellOptionsPanel.DataContext = this;
        InitializeSettingsMetadata();
        BuildSettingsFilterButtons();

        Loaded += (_, _) =>
        {
            CaptureDeviceCardPositions();
            UpdateTrayIconState();

            if (_isFirstRun || _setupForceShowingOverlay)
            {
                ShowSetupOverlay();
            }
            else
            {
                _ = _vm.InitializeAsync();
            }

            _ = RefreshVersionInfoAsync();
        };
        Closing += OnWindowClosing;
        StateChanged += OnWindowStateChanged;
        IsVisibleChanged += (_, _) => UpdateTrayIconState();
        Closed += (_, _) =>
        {
            _vm.ServiceReconnected -= OnServiceReconnectedDuringSetup;
            _vm.Devices.CollectionChanged -= Devices_CollectionChanged;
            _vm.Dispose(); // Stops auto-attach manager and device stream first.
            RunDetachOnExitCleanup();
            _tray.Dispose();
        };

        ApplySettingsSearch();
    }

    public void StartMinimizedToTrayIfEnabled()
    {
        if (!MinimizeToTrayEnabled)
        {
            WindowState = WindowState.Minimized;
            return;
        }

        HideToTray();
    }

    public void ForceShowSetup()
    {
        ShowSetupOverlay();
    }

    public void ResetSetupFlag()
    {
        _settings.SetupCompleted = false;
        _settingsService.Save(_settings);
    }

    public void RestoreAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        UpdateTrayIconState();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_exitingFromTray)
        {
            return;
        }

        if (SetupOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        if (MinimizeToTrayEnabled)
        {
            e.Cancel = true;
            HideToTray();
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && MinimizeToTrayEnabled)
        {
            HideToTray();
        }
    }

    private void HideToTray()
    {
        Hide();
        UpdateTrayIconState();
    }

    private void ToggleWindowFromTray()
    {
        if (IsVisible)
        {
            HideToTray();
            return;
        }

        RestoreAndActivate();
    }

    private void OpenSettingsFromTray()
    {
        RestoreAndActivate();
        OpenSettingsButton_OnClick(this, new RoutedEventArgs());
    }

    private void UpdateTrayIconState()
    {
        if (!MinimizeToTrayEnabled)
        {
            _tray.HideIcon();
            return;
        }

        _tray.ShowIcon();
        _tray.UpdateShowHideMenuText(IsVisible);
    }

    private void ExitFromTray()
    {
        _exitingFromTray = true;
        _tray.HideIcon();
        Close();
    }

    private void RunDetachOnExitCleanup()
    {
        if (!_settings.DetachOnExit)
            return;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var ct = cts.Token;

            var cleanupTask = Task.Run(async () =>
            {
                IReadOnlyList<Usbdevicebridge.V1.Device> devices;
                try
                {
                    devices = await _deviceManager.GetDevicesAsync(ct);
                }
                catch
                {
                    return; // Cannot enumerate devices — skip cleanup.
                }

                var tasks = new List<Task>();
                foreach (var dev in devices)
                {
                    var state = dev.State?.ToLowerInvariant() ?? "";
                    var busId = dev.BusId;
                    if (string.IsNullOrEmpty(busId))
                        continue;

                    var hardwareId = dev.HardwareId ?? "";

                    if (state == "attached")
                    {
                        tasks.Add(CleanupDeviceAsync(busId, hardwareId, detach: true, unbind: true, ct));
                    }
                    else if (state == "shared")
                    {
                        tasks.Add(CleanupDeviceAsync(busId, hardwareId, detach: false, unbind: true, ct));
                    }
                }

                if (tasks.Count > 0)
                    await Task.WhenAll(tasks);
            }, ct);

            cleanupTask.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Failures during cleanup must not block shutdown.
        }
    }

    private async Task CleanupDeviceAsync(string busId, string hardwareId, bool detach, bool unbind, CancellationToken ct)
    {
        try
        {
            if (detach)
            {
                await _deviceManager.DetachAsync(busId, ct);
            }

            if (unbind)
            {
                try
                {
                    await _client.Admin.UnbindDeviceAsync(
                        new UnbindDeviceRequest { BusId = busId, HardwareId = hardwareId },
                        cancellationToken: ct);
                }
                catch
                {
                    // Service unavailable at exit — skip unbind.
                }
            }
        }
        catch
        {
            // Individual device cleanup failure is logged but does not block shutdown.
        }
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task RefreshVersionInfoAsync()
    {
        if (_isRefreshingVersionInfo)
        {
            return;
        }

        _isRefreshingVersionInfo = true;
        try
        {
            var response = await _client.Device.GetVersionInfoAsync(new GetVersionInfoRequest());
            BackendVersion = NormalizeVersion(response.ServiceVersion);
            WslVersion = NormalizeVersion(response.WslVersion);
            UsbIpdVersion = NormalizeVersion(response.UsbipdVersion);
        }
        catch
        {
            BackendVersion = "Unknown";
            WslVersion = "Unknown";
            UsbIpdVersion = "Unknown";
        }
        finally
        {
            _isRefreshingVersionInfo = false;
        }
    }

    private static string NormalizeVersion(string? value)
        => string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Trim();

    private string BuildVersionInfoText()
        => string.Join(Environment.NewLine,
        [
            "Version Information",
            string.Empty,
            $"App (Frontend): {FrontendVersion}",
            $"Service (Backend): {BackendVersion}",
            $"WSL: {WslVersion}",
            $"usbipd: {UsbIpdVersion}",
        ]);

    private void CopyVersionInfo_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildVersionInfoText());
            ShowThemedNoticeDialog(
                "Version info copied",
                "Version details have been copied to your clipboard.",
                "OK");
        }
        catch (Exception ex)
        {
            ShowThemedNoticeDialog(
                "Copy failed",
                $"Unable to copy version info. {ex.Message}",
                "OK");
        }
    }

    private async void CheckUsbIp_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        var originalContent = button.Content;
        var originalStatus = _vm.StatusText;
        button.IsEnabled = false;
        button.Content = "Checking...";
        _vm.StatusText = "Checking prerequisites";

        try
        {
            var response = await GetPrerequisitesStatusAsync();
            _setupPrerequisitesStatus = response.Prerequisites
                .Select(p => (p.Name, p.Status, p.Message))
                .ToList();
            _setupPrerequisitesVerifiedInstalled = response.Prerequisites.All(
                p => string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase));

            ShowThemedNoticeDialog(
                "USBIP setup status",
                BuildPrerequisitesStatusMessage(response),
                "OK");
        }
        catch (RpcException ex)
        {
            ShowThemedNoticeDialog(
                "USBIP setup status",
                $"Unable to check setup status. Service error: {ex.Status.Detail}",
                "OK");
        }
        catch (Exception ex)
        {
            ShowThemedNoticeDialog(
                "USBIP setup status",
                $"Unable to check setup status. {ex.Message}",
                "OK");
        }
        finally
        {
            button.IsEnabled = true;
            button.Content = originalContent;
            _vm.StatusText = originalStatus;
        }
    }

    private async Task<CheckPrerequisitesResponse> GetPrerequisitesStatusAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        return await _client.Setup.CheckPrerequisitesAsync(
            new CheckPrerequisitesRequest(),
            deadline: deadline);
    }

    private static string BuildPrerequisitesStatusMessage(CheckPrerequisitesResponse response)
    {
        if (response.Prerequisites.Count == 0)
            return "No prerequisite status information was returned by the service.";

        var lines = new List<string>
        {
            response.AllMet
                ? "All prerequisites are installed."
                : "Some prerequisites are missing or need attention.",
            string.Empty,
        };

        foreach (var prereq in response.Prerequisites)
        {
            var installed = string.Equals(prereq.Status, "installed", StringComparison.OrdinalIgnoreCase);
            var marker = installed ? "[OK]" : "[MISSING]";
            var details = string.IsNullOrWhiteSpace(prereq.Message) ? prereq.Status : prereq.Message;
            lines.Add($"{marker} {prereq.Name}: {details}");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private void OpenUsbIpDocs_OnClick(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://github.com/dorssel/usbipd-win",
            UseShellExecute = true,
        });
    }

    private async void ResetSetup_OnClick(object sender, RoutedEventArgs e)
    {
        var confirmed = ShowThemedConfirmationDialog(
            "Reset settings",
            "This will reset all settings and forget all remembered devices. Continue?",
            "Reset",
            "Cancel");

        if (!confirmed)
        {
            return;
        }

        var resetResult = await _settingsResetService.ResetAsync();
        if (!resetResult.Succeeded)
        {
            ShowThemedNoticeDialog(
                "Reset failed",
                resetResult.ErrorMessage,
                "OK");
            return;
        }

        _vm.IsAutoRefresh = false;

        if (!TryScheduleRestartAfterShutdown(out var restartError))
        {
            ShowThemedNoticeDialog(
                "Reset completed",
                $"Settings were reset, but restart failed. {restartError}",
                "OK");
            return;
        }

        System.Windows.Application.Current.Shutdown();
    }

    private bool ShowThemedConfirmationDialog(string title, string message, string confirmText, string cancelText)
    {
        var dialogResult = ShowThemedDialog(title, message, confirmText, cancelText);
        return dialogResult == true;
    }

    private void ShowThemedNoticeDialog(string title, string message, string buttonText)
    {
        _ = ShowThemedDialog(title, message, buttonText, null);
    }

    private bool? ShowThemedDialog(string title, string message, string primaryButtonText, string? secondaryButtonText)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            MinWidth = 440,
            MaxWidth = 560,
        };

        var border = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            Padding = new Thickness(20),
        };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 10),
        };

        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 18),
        };

        var buttonRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        if (!string.IsNullOrWhiteSpace(secondaryButtonText))
        {
            var secondaryButton = new System.Windows.Controls.Button
            {
                Content = secondaryButtonText,
                Style = (Style)FindResource("GhostBtn"),
                Width = 100,
                Margin = new Thickness(0, 0, 8, 0),
            };
            secondaryButton.Click += (_, _) =>
            {
                dialog.DialogResult = false;
                dialog.Close();
            };
            buttonRow.Children.Add(secondaryButton);
        }

        var primaryButton = new System.Windows.Controls.Button
        {
            Content = primaryButtonText,
            Style = (Style)FindResource("AccentBtn"),
            Width = 100,
        };
        primaryButton.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };
        buttonRow.Children.Add(primaryButton);

        root.Children.Add(titleBlock);
        Grid.SetRow(messageBlock, 1);
        root.Children.Add(messageBlock);
        Grid.SetRow(buttonRow, 2);
        root.Children.Add(buttonRow);

        border.Child = root;
        dialog.Content = border;

        return dialog.ShowDialog();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private Task<FirewallConsentDecision> RequestFirewallConsentAsync(FirewallConsentRequest request)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => RequestFirewallConsentAsync(request)).Task.Unwrap();

        var title = request.IsAutoAttach
            ? "Auto-attach firewall recovery"
            : "Firewall recovery";

        var body = request.IsAutoAttach
            ? $"Auto-attach for \"{request.DeviceDescription}\" appears blocked by Windows Firewall.\n\nAllow a one-time firewall adjustment and retry now?"
            : $"Attach for \"{request.DeviceDescription}\" appears blocked by Windows Firewall.\n\nAllow a one-time firewall adjustment and retry now?";

        var decision = ShowFirewallConsentDialog(title, body);
        if (decision.RememberChoice)
        {
            _settings.FirewallFixPolicy = decision.AllowNow
                ? FirewallFixPolicies.Always
                : FirewallFixPolicies.Never;
            _settingsService.Save(_settings);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirewallFixPolicySelected)));
        }

        return Task.FromResult(decision);
    }

    private Task<ForceRetryDecision> RequestForceRetryAsync(ForceRetryRequest request)
    {
        if (!Dispatcher.CheckAccess())
            return Dispatcher.InvokeAsync(() => RequestForceRetryAsync(request)).Task.Unwrap();

        var stageText = request.Stage == ForceRetryStage.Bind ? "bind" : "attach";
        var title = request.IsAutoAttach
            ? "Auto-attach busy device"
            : "Busy device detected";

        var deviceText = string.IsNullOrWhiteSpace(request.DeviceDescription)
            ? request.InstanceId
            : request.DeviceDescription;

        var body = request.IsAutoAttach
            ? $"Auto-attach {stageText} for \"{deviceText}\" reported a busy device and supports --force.\n\nRetry {stageText} with --force now?"
            : $"{stageText[..1].ToUpperInvariant()}{stageText[1..]} for \"{deviceText}\" reported a busy device and supports --force.\n\nRetry {stageText} with --force now?";

        var retryWithForce = ShowForceRetryDialog(title, body);
        return Task.FromResult(new ForceRetryDecision(RetryWithForce: retryWithForce));
    }

    private bool ShowForceRetryDialog(string title, string message)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            MinWidth = 460,
            MaxWidth = 640,
        };

        var border = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            Padding = new Thickness(20),
        };

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 14),
        });

        var buttonRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "Cancel",
            Style = (Style)FindResource("GhostBtn"),
            Width = 110,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var retryButton = new System.Windows.Controls.Button
        {
            Content = "Retry with --force",
            Style = (Style)FindResource("AccentBtn"),
            Width = 170,
        };

        var retryWithForce = false;
        cancelButton.Click += (_, _) =>
        {
            retryWithForce = false;
            dialog.DialogResult = false;
            dialog.Close();
        };
        retryButton.Click += (_, _) =>
        {
            retryWithForce = true;
            dialog.DialogResult = true;
            dialog.Close();
        };

        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(retryButton);
        root.Children.Add(buttonRow);

        border.Child = root;
        dialog.Content = border;

        _ = dialog.ShowDialog();
        return retryWithForce;
    }

    private FirewallConsentDecision ShowFirewallConsentDialog(string title, string message)
    {
        var dialog = new Window
        {
            Owner = this,
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            SizeToContent = SizeToContent.WidthAndHeight,
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            MinWidth = 460,
            MaxWidth = 620,
        };

        var border = new Border
        {
            BorderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Background = (System.Windows.Media.Brush)FindResource("SurfaceBg"),
            Padding = new Thickness(20),
        };

        var root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (System.Windows.Media.Brush)FindResource("TextSecondary"),
            Margin = new Thickness(0, 0, 0, 12),
        });

        var rememberCheck = new System.Windows.Controls.CheckBox
        {
            Content = "Remember my decision",
            Style = (Style)FindResource("ModernCheckBox"),
            Margin = new Thickness(0, 0, 0, 14),
        };
        root.Children.Add(rememberCheck);

        var buttonRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
        };

        var denyButton = new System.Windows.Controls.Button
        {
            Content = "Not now",
            Style = (Style)FindResource("GhostBtn"),
            Width = 110,
            Margin = new Thickness(0, 0, 8, 0),
        };
        var allowButton = new System.Windows.Controls.Button
        {
            Content = "Allow and retry",
            Style = (Style)FindResource("AccentBtn"),
            Width = 140,
        };

        var allow = false;
        denyButton.Click += (_, _) =>
        {
            allow = false;
            dialog.DialogResult = false;
            dialog.Close();
        };
        allowButton.Click += (_, _) =>
        {
            allow = true;
            dialog.DialogResult = true;
            dialog.Close();
        };

        buttonRow.Children.Add(denyButton);
        buttonRow.Children.Add(allowButton);
        root.Children.Add(buttonRow);

        border.Child = root;
        dialog.Content = border;

        _ = dialog.ShowDialog();
        return new FirewallConsentDecision(
            AllowNow: allow,
            RememberChoice: rememberCheck.IsChecked == true);
    }
}
