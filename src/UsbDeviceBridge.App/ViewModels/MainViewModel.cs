using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using UsbDeviceBridge.App.Models;
using UsbDeviceBridge.App.Services;
using Usbdevicebridge.V1;
using WpfApplication = System.Windows.Application;

namespace UsbDeviceBridge.App.ViewModels;

public enum ToastKind { Success, Error, Warning, Info }

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly BridgeServiceClient _client;
    private readonly LocalDeviceManager _deviceManager;
    private readonly AppRememberedDeviceStore _rememberedStore;
    private readonly LocalAutoAttachManager _autoAttachManager;
    private readonly NotificationService _notificationService;
    private readonly Func<Task<bool>>? _triggerServiceRestartAsync;
    private readonly Func<string>? _getFirewallFixPolicy;
    private readonly Func<FirewallConsentRequest, Task<FirewallConsentDecision>>? _requestFirewallConsentAsync;
    private readonly Func<ForceRetryRequest, Task<ForceRetryDecision>>? _requestForceRetryAsync;
    private readonly Func<bool> _isWindowFocused;
    private readonly Func<string, string, Models.NotificationSeverity, Task> _dispatchOsNotificationAsync;
    private readonly Dictionary<string, Usbdevicebridge.V1.Device> _streamDevices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busyIds = new();

    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private CancellationTokenSource? _heartbeatCts;
    private Task? _heartbeatTask;
    private CancellationTokenSource? _feedbackCts;
    private bool? _isServiceConnected;

    private string _sortOrder = "State then name";

    public event Action? ToastShown;
    public event Action? ToastDismissRequested;
    public event Action? ServiceReconnected;

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();

    public NotificationService NotificationService => _notificationService;

    [ObservableProperty] private string _statusText = "Connecting to service...";
    [ObservableProperty] private string _feedbackMessage = "";
    [ObservableProperty] private ToastKind _feedbackKind = ToastKind.Success;
    [ObservableProperty] private bool _isAutoRefresh = true;

    [ObservableProperty] private bool _isReconnectPanelVisible;
    [ObservableProperty] private bool _areDeviceActionsEnabled = true;
    [ObservableProperty] private string _serviceRecoveryMessage = "";
    [ObservableProperty] private string _serviceRecoveryDetails = "";
    [ObservableProperty] private bool _isRestartServiceInProgress;

    [ObservableProperty] private Visibility _loadingVisibility = Visibility.Visible;
    [ObservableProperty] private Visibility _listVisibility = Visibility.Collapsed;
    [ObservableProperty] private Visibility _emptyVisibility = Visibility.Collapsed;

    public MainViewModel(
        BridgeServiceClient client,
        LocalDeviceManager deviceManager,
        AppRememberedDeviceStore rememberedStore,
        LocalAutoAttachManager autoAttachManager,
        Func<Task<bool>>? triggerServiceRestartAsync = null,
        Func<string>? getFirewallFixPolicy = null,
        Func<FirewallConsentRequest, Task<FirewallConsentDecision>>? requestFirewallConsentAsync = null,
        Func<ForceRetryRequest, Task<ForceRetryDecision>>? requestForceRetryAsync = null,
        Func<bool>? isWindowFocused = null,
        Func<string, string, Models.NotificationSeverity, Task>? dispatchOsNotificationAsync = null)
    {
        _client = client;
        _deviceManager = deviceManager;
        _rememberedStore = rememberedStore;
        _autoAttachManager = autoAttachManager;
        _triggerServiceRestartAsync = triggerServiceRestartAsync;
        _getFirewallFixPolicy = getFirewallFixPolicy;
        _requestFirewallConsentAsync = requestFirewallConsentAsync;
        _requestForceRetryAsync = requestForceRetryAsync;
        _isWindowFocused = isWindowFocused ?? (() => true);
        _dispatchOsNotificationAsync = dispatchOsNotificationAsync ?? ((_, _, _) => Task.CompletedTask);
        _notificationService = new NotificationService();

        _autoAttachManager.AttachingStateChanged += OnAutoAttachingStateChanged;
        _autoAttachManager.AutoAttachFailed += OnAutoAttachFailed;
        _autoAttachManager.AutoAttachNotification += OnAutoAttachNotification;
    }

    partial void OnIsAutoRefreshChanged(bool value)
    {
        if (value)
            StartDeviceStream();
        else
            StopDeviceStream();
    }

    [RelayCommand]
    private void DismissToast()
    {
        _feedbackCts?.Cancel();
        ToastDismissRequested?.Invoke();
    }

    [RelayCommand]
    private async Task RestartServiceAsync()
    {
        if (_triggerServiceRestartAsync is null || IsRestartServiceInProgress)
            return;

        IsRestartServiceInProgress = true;
        var restartInProgress = ServiceRecoveryTextFactory.RestartInProgress();
        SetServiceRecoveryState(
            isVisible: true,
            restartInProgress.Message,
            restartInProgress.Details,
            actionsEnabled: false);

        var started = false;
        try
        {
            started = await _triggerServiceRestartAsync();
        }
        catch (Exception ex)
        {
            ShowError($"Service restart failed: {ex.Message}");
        }
        finally
        {
            IsRestartServiceInProgress = false;
        }

        if (!started)
        {
            var cancelled = ServiceRecoveryTextFactory.RestartCancelledOrBlocked();
            SetServiceRecoveryState(
                isVisible: true,
                cancelled.Message,
                cancelled.Details,
                actionsEnabled: false);
            return;
        }

        var reconnecting = ServiceRecoveryTextFactory.Reconnecting();
        SetServiceRecoveryState(
            isVisible: true,
            reconnecting.Message,
            reconnecting.Details,
            actionsEnabled: false);

        if (_streamTask is null || _streamTask.IsCompleted)
            ServiceReconnected?.Invoke();
    }

    public void ClearFeedback()
    {
        FeedbackMessage = "";
    }

    public void ShowServiceRecoveryPromptForSetup(string? detailOverride = null)
    {
        var text = ServiceRecoveryTextFactory.ServiceNotRunning();
        SetServiceRecoveryState(
            isVisible: true,
            text.Message,
            string.IsNullOrWhiteSpace(detailOverride) ? text.Details : detailOverride,
            actionsEnabled: false);
    }

    public void HideServiceRecoveryPromptForSetup()
    {
        SetServiceRecoveryState(isVisible: false, message: "", details: "", actionsEnabled: true);
    }

    public void SetSortOrder(string? sortOrder)
    {
        _sortOrder = string.Equals(sortOrder, "Name", StringComparison.OrdinalIgnoreCase)
            ? "Name"
            : "State then name";

        if (_streamDevices.Count > 0)
            UpdateDeviceList(_streamDevices.Values);
    }

    public async Task InitializeAsync()
    {
        await RefreshAsync();
        if (IsAutoRefresh)
            StartDeviceStream();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var devices = await _deviceManager.GetDevicesAsync(CancellationToken.None);
            var remembered = _rememberedStore.Load();
            ApplyRememberedState(devices, remembered);
            ReplaceStreamSnapshot(devices);
            UpdateDeviceList(devices);

            StatusText = devices.Count == 1 ? "1 device" : $"{devices.Count} devices";
        }
        catch (Exception ex)
        {
            ShowError($"Refresh failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ConnectAsync(DeviceViewModel? dev)
    {
        if (dev is null || !AreDeviceActionsEnabled || dev.IsBusy || string.IsNullOrEmpty(dev.BusId))
            return;

        _busyIds.Add(dev.InstanceId);
        dev.IsBusy = true;

        var didBind = false;
        try
        {
            var policy = NormalizeFirewallPolicy(_getFirewallFixPolicy?.Invoke());

            // Device must be in "shared" state for attach; bind first if still "available".
            if (string.Equals(dev.State, "available", StringComparison.OrdinalIgnoreCase))
            {
                var bindResult = await BindWithForceRetryIfNeededAsync(
                    dev.Description,
                    dev.InstanceId,
                    dev.BusId,
                    string.Empty,
                    isAutoAttach: false,
                    CancellationToken.None);

                if (!bindResult.Ok)
                {
                    if (bindResult.UseWarning)
                        ShowWarning(bindResult.Message);
                    else
                        ShowError(bindResult.Message);
                    return;
                }

                didBind = true;
            }

            var response = await AttachWithPolicyAsync(
                dev.Description,
                dev.InstanceId,
                dev.BusId,
                string.Empty,
                policy,
                isAutoAttach: false);

            if (!response.Ok)
            {
                // Attach failed — unbind to avoid leaving device in "shared" state.
                if (didBind)
                    await TryUnbindAsync(dev.BusId, dev.HardwareId);

                if (response.UseWarning)
                    ShowWarning(response.Message);
                else
                    ShowError(MapManualAttachError(dev.Description, response));
            }
        }
        catch (RpcException ex)
        {
            if (didBind)
                await TryUnbindAsync(dev.BusId, dev.HardwareId);
            ShowError($"Connect failed: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            if (didBind)
                await TryUnbindAsync(dev.BusId, dev.HardwareId);
            ShowError($"Connect failed: {ex.Message}");
        }
        finally
        {
            _busyIds.Remove(dev.InstanceId);
            dev.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync(DeviceViewModel? dev)
    {
        if (dev is null || !AreDeviceActionsEnabled || dev.IsBusy || string.IsNullOrEmpty(dev.BusId))
            return;

        _busyIds.Add(dev.InstanceId);
        dev.IsBusy = true;

        try
        {
            // Detach first (app context, no elevation needed).
            var (detachOk, detachMsg) = await _deviceManager.DetachAsync(dev.BusId, CancellationToken.None);
            if (!detachOk)
            {
                ShowError($"Detach failed: {(detachMsg.Length > 0 ? detachMsg : "Detach failed.")}");
                return;
            }

            // Then unbind (service, requires elevation). BUG-0007 fix.
            try
            {
                var unbindResp = await _client.Admin.UnbindDeviceAsync(
                    new UnbindDeviceRequest { BusId = dev.BusId, HardwareId = dev.HardwareId },
                    cancellationToken: CancellationToken.None);

                if (unbindResp.Ok)
                    ShowOk($"Disconnected {dev.Description}.");
                else
                    ShowWarning($"Detached but unbind failed: {unbindResp.Message}");
            }
            catch (RpcException rpcEx)
            {
                ShowWarning($"Detached but unbind RPC failed: {rpcEx.Status.Detail}");
            }
        }
        catch (RpcException ex)
        {
            ShowError($"Disconnect failed: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            ShowError($"Disconnect failed: {ex.Message}");
        }
        finally
        {
            _busyIds.Remove(dev.InstanceId);
            dev.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ToggleRememberAsync(DeviceViewModel? dev)
    {
        if (dev is null || !AreDeviceActionsEnabled || string.IsNullOrWhiteSpace(dev.InstanceId))
            return;

        try
        {
            if (dev.Remembered)
            {
                // Forget: release device if attached/shared, then unbind.
                IReadOnlyList<Usbdevicebridge.V1.Device>? devices = null;
                try { devices = await _deviceManager.GetDevicesAsync(CancellationToken.None); } catch { }

                var device = devices?.FirstOrDefault(d =>
                    string.Equals(d.InstanceId, dev.InstanceId, StringComparison.OrdinalIgnoreCase));

                if (device is not null && device.State == "attached")
                {
                    var (detachOk, _) = await _deviceManager.DetachAsync(device.BusId, CancellationToken.None);
                    if (!detachOk)
                    {
                        ShowError("Cannot forget: detach failed.");
                        return;
                    }
                }

                if (device is not null && device.State != "available")
                {
                    var unbindResp = await _client.Admin.UnbindDeviceAsync(
                        new UnbindDeviceRequest { BusId = device.BusId, HardwareId = device.HardwareId },
                        cancellationToken: CancellationToken.None);
                    if (!unbindResp.Ok)
                        ShowWarning("Unbind failed but device forgotten locally.");
                }

                _rememberedStore.Remove(dev.InstanceId);
                ShowInfo($"Forgot {dev.Description}.");
            }
            else
            {
                _rememberedStore.AddOrUpdate(dev.InstanceId, string.Empty);
                ShowInfo($"Remembered {dev.Description}.");
            }

            await RefreshAsync();
        }
        catch (RpcException ex)
        {
            ShowError($"Remember operation failed: {ex.Status.Detail}");
        }
        catch (Exception ex)
        {
            ShowError($"Remember operation failed: {ex.Message}");
        }
    }

    private void StartDeviceStream()
    {
        if (_streamTask is not null && !_streamTask.IsCompleted)
            return;

        _streamCts = new CancellationTokenSource();
        _streamTask = Task.Run(() => StreamLoopAsync(_streamCts.Token));
        StartHeartbeatMonitor();
        _autoAttachManager.Start();
    }

    private void StopDeviceStream()
    {
        _autoAttachManager.Stop();
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;

        _heartbeatCts?.Cancel();
        _heartbeatCts?.Dispose();
        _heartbeatCts = null;
    }

    private void StartHeartbeatMonitor()
    {
        if (_heartbeatTask is not null && !_heartbeatTask.IsCompleted)
            return;

        _heartbeatCts = new CancellationTokenSource();
        _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_heartbeatCts.Token));
    }

    private async Task HeartbeatLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var call = _client.Device.WatchHeartbeat(
                    new HeartbeatRequest { IntervalMs = 2000 },
                    cancellationToken: token);

                while (await call.ResponseStream.MoveNext(token))
                {
                    TransitionToConnected();
                }

                // Stream ended unexpectedly; treat as disconnected and retry.
                var reconnecting = ServiceRecoveryTextFactory.Reconnecting();
                TransitionToDisconnected(reconnecting, "Reconnecting...");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
            {
                var serviceNotRunning = ServiceRecoveryTextFactory.ServiceNotRunning();
                TransitionToDisconnected(serviceNotRunning, "Service unavailable");
            }
            catch
            {
                var reconnecting = ServiceRecoveryTextFactory.Reconnecting();
                TransitionToDisconnected(reconnecting, "Reconnecting...");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task StreamLoopAsync(CancellationToken token)
    {
        const int pollIntervalMs = 800;
        var debounceWindow = TimeSpan.FromMilliseconds(1200);

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Initial snapshot.
                var snapshot = await _deviceManager.GetDevicesAsync(token);
                var remembered = _rememberedStore.Load();
                ApplyRememberedState(snapshot, remembered);
                ReplaceStreamSnapshot(snapshot);
                UpdateDeviceList(snapshot);

                StatusText = snapshot.Count == 1 ? "1 device" : $"{snapshot.Count} devices";

                // Polling loop (replaces service-side StreamDevices).
                var lastSnapshot = snapshot.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
                var pending = new Dictionary<string, Usbdevicebridge.V1.Device>(StringComparer.OrdinalIgnoreCase);
                DateTimeOffset? pendingSince = null;

                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(pollIntervalMs, token);

                    var current = await _deviceManager.GetDevicesAsync(token);
                    remembered = _rememberedStore.Load();
                    ApplyRememberedState(current, remembered);

                    // Detect additions/changes.
                    foreach (var dev in current)
                    {
                        if (!lastSnapshot.TryGetValue(dev.InstanceId, out var last)
                            || last.State != dev.State
                            || last.Remembered != dev.Remembered
                            || last.Attaching != dev.Attaching)
                        {
                            pending[dev.InstanceId] = dev;
                        }
                    }

                    // Detect removals.
                    foreach (var instanceId in lastSnapshot.Keys
                        .Except(current.Select(d => d.InstanceId), StringComparer.OrdinalIgnoreCase))
                    {
                        pending[instanceId] = new Usbdevicebridge.V1.Device { InstanceId = instanceId };
                    }

                    var now = DateTimeOffset.UtcNow;
                    if (pending.Count > 0)
                        pendingSince ??= now;

                    var shouldFlush = pending.Count >= 25
                        || (pendingSince.HasValue && now - pendingSince.Value >= debounceWindow);

                    if (shouldFlush)
                    {
                        UpdateDeviceList(current);
                        ReplaceStreamSnapshot(current);
                        pending.Clear();
                        pendingSince = null;
                    }

                    lastSnapshot = current.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);
                    StatusText = current.Count == 1 ? "1 device" : $"{current.Count} devices";
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                var reconnectingState = ServiceRecoveryTextFactory.Reconnecting();
                TransitionToDisconnected(reconnectingState, "Reconnecting...");
                ShowError($"Device polling error: {ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(2), token); } catch { }
            }
        }
    }

    private void OnAutoAttachingStateChanged(string instanceId, bool isAttaching)
    {
        // Propagate attaching state from LocalAutoAttachManager into the device list.
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnAutoAttachingStateChanged(instanceId, isAttaching));
            return;
        }

        var vm = Devices.FirstOrDefault(d =>
            string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));
        if (vm is not null)
            vm.IsAttaching = isAttaching;
    }

    private void OnAutoAttachFailed(string instanceId, string message)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnAutoAttachFailed(instanceId, message));
            return;
        }

        ShowWarning(message);
    }

    private void OnAutoAttachNotification(string message, NotificationSeverity severity)
    {
        var dispatcher = WpfApplication.Current?.Dispatcher;
        if (dispatcher is null)
            return;

        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => OnAutoAttachNotification(message, severity));
            return;
        }

        if (severity == NotificationSeverity.Error)
            ShowError(message);
        else if (severity == NotificationSeverity.Warning)
            ShowWarning(message);
        else
            ShowInfo(message);
    }

    private void ApplyRememberedState(
        IReadOnlyList<Usbdevicebridge.V1.Device> devices,
        Dictionary<string, string> remembered)
    {
        foreach (var dev in devices)
        {
            dev.Remembered = !string.IsNullOrEmpty(dev.InstanceId)
                && remembered.ContainsKey(dev.InstanceId);
            dev.PreferredDistro = dev.Remembered
                && remembered.TryGetValue(dev.InstanceId, out var distro) ? distro : "";
        }
    }

    private void ReplaceStreamSnapshot(IReadOnlyList<Usbdevicebridge.V1.Device> devices)
    {
        _streamDevices.Clear();
        foreach (var dev in devices)
            _streamDevices[dev.InstanceId] = dev;
    }

    private void ReplaceStreamSnapshot(IReadOnlyCollection<Usbdevicebridge.V1.Device> devices)
    {
        _streamDevices.Clear();
        foreach (var dev in devices)
            _streamDevices[dev.InstanceId] = dev;
    }

    private void UpdateDeviceList(IReadOnlyCollection<Usbdevicebridge.V1.Device> devices)
    {
        var sorted = SortDevices(devices);

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            Devices.Clear();

            foreach (var d in sorted)
            {
                var vm = new DeviceViewModel
                {
                    InstanceId = d.InstanceId,
                    BusId = d.BusId,
                    Description = d.Description,
                    HardwareId = d.HardwareId,
                    State = d.State,
                    Remembered = d.Remembered,
                    PreferredDistro = d.PreferredDistro,
                    IsAttaching = d.Attaching,
                    IsBusy = _busyIds.Contains(d.InstanceId),
                    DistroListReady = true,
                    SelectedDistro = string.Empty,
                };

                Devices.Add(vm);
            }

            SetListState(loading: false, empty: Devices.Count == 0, hasItems: Devices.Count > 0);
        });
    }

    private List<Usbdevicebridge.V1.Device> SortDevices(IEnumerable<Usbdevicebridge.V1.Device> devices)
        => DeviceSorter.Sort(devices, _sortOrder);

    private void SetListState(bool loading, bool empty, bool hasItems)
    {
        LoadingVisibility = loading ? Visibility.Visible : Visibility.Collapsed;
        EmptyVisibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ListVisibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetServiceRecoveryState(bool isVisible, string message, string details, bool actionsEnabled)
    {
        IsReconnectPanelVisible = isVisible;
        ServiceRecoveryMessage = message;
        ServiceRecoveryDetails = details;
        AreDeviceActionsEnabled = actionsEnabled;
    }

    private void TransitionToConnected()
    {
        var previous = _isServiceConnected;
        _isServiceConnected = true;
        SetServiceRecoveryState(isVisible: false, message: "", details: "", actionsEnabled: true);

        if (previous == false)
        {
            ShowInfo("Connected to the background service.");
            ServiceReconnected?.Invoke();
        }
    }

    private void TransitionToDisconnected(ServiceRecoveryText recoveryText, string status)
    {
        var previous = _isServiceConnected;
        _isServiceConnected = false;

        SetServiceRecoveryState(
            isVisible: true,
            message: recoveryText.Message,
            details: recoveryText.Details,
            actionsEnabled: false);
        StatusText = status;

        if (previous == true)
            ShowError("Lost connection to the background service.");
    }

    private void ShowToast(string message, ToastKind kind, NotificationSeverity severity)
    {
        var dispatcher = WpfApplication.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ShowToast(message, kind, severity));
            return;
        }

        // Strip whitespace from message to ensure clean display in notifications
        var cleanMessage = message.Trim();

        FeedbackMessage = cleanMessage;
        FeedbackKind = kind;
        ToastShown?.Invoke();

        _notificationService.AddNotification(cleanMessage, severity, "Main");

        // Dispatch OS notification when window is not focused
        if (!_isWindowFocused())
        {
            _ = _dispatchOsNotificationAsync("USB Device Bridge", cleanMessage, severity);
        }

        _feedbackCts?.Cancel();
        _feedbackCts = new CancellationTokenSource();
        var token = _feedbackCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(2800, token);
                await WpfApplication.Current.Dispatcher.InvokeAsync(() => ToastDismissRequested?.Invoke());
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    private void ShowOk(string message) => ShowToast(message, ToastKind.Success, NotificationSeverity.Info);
    private void ShowInfo(string message) => ShowToast(message, ToastKind.Info, NotificationSeverity.Info);
    private void ShowWarning(string message) => ShowToast(message, ToastKind.Warning, NotificationSeverity.Warning);
    private void ShowError(string message) => ShowToast(message, ToastKind.Error, NotificationSeverity.Error);

    private async Task TryUnbindAsync(string busId, string hardwareId = "")
    {
        try
        {
            await _client.Admin.UnbindDeviceAsync(
                new UnbindDeviceRequest { BusId = busId, HardwareId = hardwareId },
                cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Best-effort unbind; service may be unavailable.
        }
    }

    private static string NormalizeFirewallPolicy(string? value)
    {
        if (string.Equals(value, "always", StringComparison.OrdinalIgnoreCase)) return "always";
        if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase)) return "never";
        return "ask";
    }

    private async Task<AttachAttemptResult> AttachWithPolicyAsync(
        string description,
        string instanceId,
        string busId,
        string distro,
        string policy,
        bool isAutoAttach)
    {
        var (ok, msg) = await _deviceManager.AttachAsync(busId, distro, CancellationToken.None);

        if (ok)
        {
            if (isAutoAttach)
                ShowInfo($"Auto-attached {description}.");
            else
                ShowOk($"Connected {description}.");

            return new AttachAttemptResult { Ok = true, Message = msg };
        }

        if (BusySignatureClassifier.IsBusyWithForceAvailable(msg))
        {
            var forceDecision = await RequestForceRetryDecisionAsync(
                description,
                instanceId,
                busId,
                distro,
                ForceRetryStage.Bind,
                isAutoAttach);

            if (!forceDecision.RetryWithForce)
            {
                return new AttachAttemptResult
                {
                    Ok = false,
                    Message = AttachToastMessages.ForceRetryCancelled(description, ForceRetryStage.Bind),
                    UseWarning = true,
                };
            }

            var forceBindResp = await _client.Admin.BindDeviceAsync(
                new BindDeviceRequest { BusId = busId, Force = true },
                cancellationToken: CancellationToken.None);

            if (!forceBindResp.Ok)
            {
                return new AttachAttemptResult
                {
                    Ok = false,
                    Message = AttachToastMessages.ForceRetryFailed(description, ForceRetryStage.Bind, forceBindResp.Message),
                };
            }

            var (retryAttachOk, retryAttachMsg) = await _deviceManager.AttachAsync(busId, distro, CancellationToken.None);
            if (!retryAttachOk)
            {
                return new AttachAttemptResult
                {
                    Ok = false,
                    Message = retryAttachMsg.Length > 0 ? retryAttachMsg : "Attach failed.",
                };
            }

            if (isAutoAttach)
                ShowInfo(AttachToastMessages.ForceRetrySucceededAutoAttach(instanceId, ForceRetryStage.Bind));
            else
                ShowInfo(AttachToastMessages.ForceRetrySucceeded(description, ForceRetryStage.Bind));

            return new AttachAttemptResult { Ok = true };
        }

        // Detect firewall block and handle based on policy.
        if (FirewallSignatureClassifier.IsFirewallBlock(msg))
        {
            if (policy == "never")
            {
                return new AttachAttemptResult
                {
                    Ok = false,
                    Message = AttachToastMessages.PolicyPrevented(description),
                    FailReason = AttachFailReason.PolicyPrevented,
                };
            }

            if (policy == "ask")
            {
                if (_requestFirewallConsentAsync is null)
                    return new AttachAttemptResult
                    {
                        Ok = false,
                        Message = msg,
                        FailReason = AttachFailReason.PolicyPrevented,
                    };

                var decision = await _requestFirewallConsentAsync(new FirewallConsentRequest(
                    DeviceDescription: description,
                    InstanceId: instanceId,
                    BusId: busId,
                    WslDistro: distro,
                    IsAutoAttach: isAutoAttach));

                if (!decision.AllowNow)
                    return new AttachAttemptResult
                    {
                        Ok = false,
                        Message = msg,
                        FailReason = AttachFailReason.PolicyPrevented,
                    };

                // User approved; retry with "always".
                return await AttachWithPolicyAsync(description, instanceId, busId, distro, "always", isAutoAttach);
            }

            // policy == "always": call service to apply the fix then retry.
            try
            {
                var fixResp = await _client.Admin.ApplyFirewallFixAsync(
                    new ApplyFirewallFixRequest(),
                    cancellationToken: CancellationToken.None);

                if (!fixResp.Ok)
                    return new AttachAttemptResult
                    {
                        Ok = false,
                        Message = $"Firewall fix failed: {fixResp.Message}",
                        FailReason = AttachFailReason.FirewallFixFailed,
                    };
            }
            catch (RpcException ex)
            {
                return new AttachAttemptResult
                {
                    Ok = false,
                    Message = $"Firewall fix failed: {ex.Status.Detail}",
                    FailReason = AttachFailReason.FirewallFixFailed,
                };
            }

            // Retry after fix.
            var (retryOk, retryMsg) = await _deviceManager.AttachAsync(busId, distro, CancellationToken.None);
            if (!retryOk)
                return new AttachAttemptResult
                {
                    Ok = false,
                    Message = retryMsg,
                    FailReason = AttachFailReason.StillFailedAfterFix,
                };

            if (isAutoAttach)
                ShowInfo(AttachToastMessages.AutoAttachFirewallFixApplied(instanceId));
            else
                ShowOk(AttachToastMessages.FirewallFixAppliedAndSucceeded(description, distro));

            return new AttachAttemptResult { Ok = true, FirewallFixApplied = true };
        }

        return new AttachAttemptResult { Ok = false, Message = msg.Length > 0 ? msg : "Attach failed." };
    }

    private string MapManualAttachError(string description, AttachAttemptResult response)
        => response.FailReason switch
        {
            AttachFailReason.PolicyPrevented => AttachToastMessages.PolicyPrevented(description),
            AttachFailReason.FirewallFixFailed => AttachToastMessages.FirewallFixFailed(description),
            AttachFailReason.StillFailedAfterFix => AttachToastMessages.StillFailedAfterFix(description),
            _ => response.Message.Length > 0 ? response.Message : "Attach failed.",
        };

    private async Task<AttachAttemptResult> BindWithForceRetryIfNeededAsync(
        string description,
        string instanceId,
        string busId,
        string distro,
        bool isAutoAttach,
        CancellationToken ct)
    {
        var bindResp = await _client.Admin.BindDeviceAsync(
            new BindDeviceRequest { BusId = busId, Force = false },
            cancellationToken: ct);
        if (bindResp.Ok)
            return new AttachAttemptResult { Ok = true };

        if (!BusySignatureClassifier.IsBusyWithForceAvailable(bindResp.Message))
        {
            return new AttachAttemptResult
            {
                Ok = false,
                Message = $"Bind failed: {bindResp.Message}",
            };
        }

        var forceDecision = await RequestForceRetryDecisionAsync(
            description,
            instanceId,
            busId,
            distro,
            ForceRetryStage.Bind,
            isAutoAttach);

        if (!forceDecision.RetryWithForce)
        {
            return new AttachAttemptResult
            {
                Ok = false,
                Message = AttachToastMessages.ForceRetryCancelled(description, ForceRetryStage.Bind),
                UseWarning = true,
            };
        }

        var forceBindResp = await _client.Admin.BindDeviceAsync(
            new BindDeviceRequest { BusId = busId, Force = true },
            cancellationToken: ct);
        if (forceBindResp.Ok)
        {
            ShowInfo(AttachToastMessages.ForceRetrySucceeded(description, ForceRetryStage.Bind));
            return new AttachAttemptResult { Ok = true };
        }

        return new AttachAttemptResult
        {
            Ok = false,
            Message = AttachToastMessages.ForceRetryFailed(description, ForceRetryStage.Bind, forceBindResp.Message),
        };
    }

    private async Task<ForceRetryDecision> RequestForceRetryDecisionAsync(
        string description,
        string instanceId,
        string busId,
        string distro,
        ForceRetryStage stage,
        bool isAutoAttach)
    {
        if (_requestForceRetryAsync is null)
            return new ForceRetryDecision(RetryWithForce: false);

        return await _requestForceRetryAsync(new ForceRetryRequest(
            DeviceDescription: description,
            InstanceId: instanceId,
            BusId: busId,
            WslDistro: distro,
            Stage: stage,
            IsAutoAttach: isAutoAttach));
    }

    public void Dispose()
    {
        _autoAttachManager.AttachingStateChanged -= OnAutoAttachingStateChanged;
        _autoAttachManager.AutoAttachFailed -= OnAutoAttachFailed;
        _autoAttachManager.AutoAttachNotification -= OnAutoAttachNotification;
        StopDeviceStream();
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
    }

    private sealed class AttachAttemptResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; } = string.Empty;
        public AttachFailReason FailReason { get; set; }
        public bool FirewallFixApplied { get; set; }
        public bool UseWarning { get; set; }
    }
}
