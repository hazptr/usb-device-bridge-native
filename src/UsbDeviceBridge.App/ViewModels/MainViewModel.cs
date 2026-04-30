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
    private readonly NotificationService _notificationService;
    private readonly Func<Task<bool>>? _triggerServiceRestartAsync;
    private readonly Dictionary<string, Usbdevicebridge.V1.Device> _streamDevices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _busyIds = new();
    private readonly Dictionary<string, string> _distroSelections = new();
    private readonly DistroLoader _distroLoader;

    private CancellationTokenSource? _streamCts;
    private Task? _streamTask;
    private CancellationTokenSource? _feedbackCts;
    private bool? _isServiceConnected;

    private string _sortOrder = "State then name";

    public event Action<string, string>? DeviceDistroSelectionChanged;
    public event Action? ToastShown;
    public event Action? ToastDismissRequested;

    public ObservableCollection<DeviceViewModel> Devices { get; } = new();
    public ObservableCollection<string> Distros { get; } = new();

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

    public MainViewModel(BridgeServiceClient client, Func<Task<bool>>? triggerServiceRestartAsync = null)
    {
        _client = client;
        _triggerServiceRestartAsync = triggerServiceRestartAsync;
        _notificationService = new NotificationService();
        _distroLoader = new DistroLoader(client, Distros);
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

        // Allow reconnect loop to recover naturally and switch panel state.
        var reconnecting = ServiceRecoveryTextFactory.Reconnecting();
        SetServiceRecoveryState(
            isVisible: true,
            reconnecting.Message,
            reconnecting.Details,
            actionsEnabled: false);
    }

    public void ClearFeedback()
    {
        FeedbackMessage = "";
    }

    public void SetSortOrder(string? sortOrder)
    {
        _sortOrder = string.Equals(sortOrder, "Name", StringComparison.OrdinalIgnoreCase)
            ? "Name"
            : "State then name";

        if (_streamDevices.Count > 0)
            UpdateDeviceList(_streamDevices.Values);
    }

    public void InitializeDistroSelections(IReadOnlyDictionary<string, string>? selections)
    {
        _distroSelections.Clear();
        if (selections is null)
            return;

        foreach (var (instanceId, distro) in selections)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(distro))
                continue;

            _distroSelections[instanceId] = distro;
        }
    }

    public async Task InitializeAsync()
    {
        await EnsureDistrosLoadedAsync(force: true);
        await RefreshAsync();
        if (IsAutoRefresh)
            StartDeviceStream();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await EnsureDistrosLoadedAsync();

        try
        {
            var response = await _client.Device.GetDevicesAsync(new GetDevicesRequest());
            ReplaceStreamSnapshot(response.Devices);
            UpdateDeviceList(response.Devices);

            TransitionToConnected();

            var count = response.Devices.Count;
            StatusText = count == 1 ? "1 device" : $"{count} devices";
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            var serviceNotRunning = ServiceRecoveryTextFactory.ServiceNotRunning();
            TransitionToDisconnected(serviceNotRunning, "Service unavailable");
            SetListState(loading: false, empty: false, hasItems: false);
        }
        catch (RpcException ex)
        {
            ShowError($"gRPC error: {ex.Status.Detail}");
        }
    }

    [RelayCommand]
    private async Task ConnectAsync(DeviceViewModel? dev)
    {
        if (dev is null || !AreDeviceActionsEnabled || dev.IsBusy || string.IsNullOrEmpty(dev.BusId))
            return;

        if (string.IsNullOrEmpty(dev.SelectedDistro))
        {
            ShowError("Select a WSL distro before connecting.");
            return;
        }

        _busyIds.Add(dev.InstanceId);
        dev.IsBusy = true;

        try
        {
            var response = await _client.Device.AttachDeviceAsync(new AttachDeviceRequest
            {
                BusId = dev.BusId,
                WslDistro = dev.SelectedDistro,
                InstanceId = dev.InstanceId,
                Remember = false,
            });

            if (response.Ok)
                ShowOk($"Connected {dev.Description} to {dev.SelectedDistro}.");
            else
                ShowError(response.Message.Length > 0 ? response.Message : "Attach failed.");
        }
        catch (RpcException ex)
        {
            ShowError($"Connect failed: {ex.Status.Detail}");
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
            var response = await _client.Device.DetachDeviceAsync(new DetachDeviceRequest
            {
                BusId = dev.BusId,
                InstanceId = dev.InstanceId,
            });

            if (response.Ok)
                ShowOk($"Disconnected {dev.Description}.");
            else
                ShowError(response.Message.Length > 0 ? response.Message : "Detach failed.");
        }
        catch (RpcException ex)
        {
            ShowError($"Disconnect failed: {ex.Status.Detail}");
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
                var forget = await _client.AutoAttach.ForgetDeviceAsync(new ForgetDeviceRequest { InstanceId = dev.InstanceId });
                if (forget.Ok)
                    ShowInfo($"Forgot {dev.Description}.");
                else
                    ShowError(forget.Message.Length > 0 ? forget.Message : "Could not forget device.");
            }
            else
            {
                var distro = string.IsNullOrWhiteSpace(dev.SelectedDistro) ? string.Empty : dev.SelectedDistro;
                var remember = await _client.AutoAttach.RememberDeviceAsync(new RememberDeviceRequest
                {
                    InstanceId = dev.InstanceId,
                    PreferredDistro = distro,
                });

                if (remember.Ok)
                    ShowInfo($"Remembered {dev.Description}{(string.IsNullOrWhiteSpace(distro) ? "" : $" for {distro}")}.");
                else
                    ShowError(remember.Message.Length > 0 ? remember.Message : "Could not remember device.");
            }

            await RefreshAsync();
        }
        catch (RpcException ex)
        {
            ShowError($"Remember operation failed: {ex.Status.Detail}");
        }
    }

    private async Task EnsureDistrosLoadedAsync(bool force = false)
    {
        await _distroLoader.EnsureLoadedAsync(force);
    }

    private void StartDeviceStream()
    {
        if (_streamTask is not null && !_streamTask.IsCompleted)
            return;

        _streamCts = new CancellationTokenSource();
        _streamTask = Task.Run(() => StreamLoopAsync(_streamCts.Token));
    }

    private void StopDeviceStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    private async Task StreamLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await EnsureDistrosLoadedAsync();
                using var stream = _client.Device.StreamDevices(new StreamDevicesRequest(), cancellationToken: token);

                // Treat the connection as valid only after a successful RPC round-trip.
                var snapshot = await _client.Device.GetDevicesAsync(
                    new GetDevicesRequest(),
                    cancellationToken: token);
                ReplaceStreamSnapshot(snapshot.Devices);
                UpdateDeviceList(snapshot.Devices);
                TransitionToConnected();

                StatusText = snapshot.Devices.Count switch
                {
                    1 => "1 device",
                    var n => $"{n} devices",
                };

                await foreach (var update in stream.ResponseStream.ReadAllAsync(token))
                {
                    ApplyDeviceEvent(update);
                    UpdateDeviceList(_streamDevices.Values);

                    StatusText = _streamDevices.Count switch
                    {
                        1 => "1 device",
                        var n => $"{n} devices",
                    };
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
            {
                var reconnectingState = ServiceRecoveryTextFactory.Reconnecting();
                TransitionToDisconnected(reconnectingState, "Service unavailable");

                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch (RpcException ex)
            {
                ShowError($"Stream error: {ex.Status.Detail}");
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
            catch
            {
                await Task.Delay(TimeSpan.FromSeconds(2), token);
            }
        }
    }

    private void ReplaceStreamSnapshot(IReadOnlyCollection<Usbdevicebridge.V1.Device> devices)
    {
        _streamDevices.Clear();
        foreach (var dev in devices)
        {
            _streamDevices[dev.InstanceId] = dev;
        }
    }

    private void ApplyDeviceEvent(DeviceEvent deviceEvent)
    {
        if (deviceEvent.Device is null || string.IsNullOrWhiteSpace(deviceEvent.Device.InstanceId))
            return;

        var eventType = deviceEvent.EventType?.ToLowerInvariant();
        if (eventType is "remove" or "deleted")
        {
            _streamDevices.Remove(deviceEvent.Device.InstanceId);
            return;
        }

        _streamDevices[deviceEvent.Device.InstanceId] = deviceEvent.Device;
    }

    private void UpdateDeviceList(IReadOnlyCollection<Usbdevicebridge.V1.Device> devices)
    {
        var sorted = SortDevices(devices);

        WpfApplication.Current.Dispatcher.Invoke(() =>
        {
            Devices.Clear();

            foreach (var d in sorted)
            {
                _distroSelections.TryGetValue(d.InstanceId, out var selectedDistro);

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
                    DistroListReady = Distros.Count > 0,
                    SelectedDistro = ChooseInitialDistro(d, selectedDistro),
                };

                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(DeviceViewModel.SelectedDistro)
                        && !string.IsNullOrWhiteSpace(vm.InstanceId)
                        && !string.IsNullOrWhiteSpace(vm.SelectedDistro))
                    {
                        _distroSelections[vm.InstanceId] = vm.SelectedDistro;
                        DeviceDistroSelectionChanged?.Invoke(vm.InstanceId, vm.SelectedDistro);
                    }
                };

                Devices.Add(vm);
            }

            SetListState(loading: false, empty: Devices.Count == 0, hasItems: Devices.Count > 0);
        });
    }

    private string ChooseInitialDistro(Usbdevicebridge.V1.Device device, string? savedSelection)
    {
        if (!string.IsNullOrWhiteSpace(savedSelection) && Distros.Contains(savedSelection))
            return savedSelection;

        if (!string.IsNullOrWhiteSpace(device.PreferredDistro) && Distros.Contains(device.PreferredDistro))
            return device.PreferredDistro;

        return Distros.FirstOrDefault() ?? string.Empty;
    }

    private List<Usbdevicebridge.V1.Device> SortDevices(IEnumerable<Usbdevicebridge.V1.Device> devices)
    {
        if (string.Equals(_sortOrder, "Name", StringComparison.OrdinalIgnoreCase))
        {
            return devices
                .OrderBy(d => d.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return devices
            .OrderBy(d => GetStateRank(d.State))
            .ThenBy(d => d.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static int GetStateRank(string state) => state.ToLowerInvariant() switch
    {
        "attached" => 0,
        "shared" => 1,
        "available" => 2,
        "offline" => 3,
        _ => 4,
    };

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
        {
            ShowError("Lost connection to the background service.");
        }
    }

    private void ShowToast(string message, ToastKind kind, NotificationSeverity severity)
    {
        var dispatcher = WpfApplication.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            dispatcher.Invoke(() => ShowToast(message, kind, severity));
            return;
        }

        FeedbackMessage = message;
        FeedbackKind = kind;
        ToastShown?.Invoke();

        _notificationService.AddNotification(message, severity, "Main");

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
            catch (OperationCanceledException)
            {
                // Ignore cancellation.
            }
        }, token);
    }

    private void ShowOk(string message) => ShowToast(message, ToastKind.Success, NotificationSeverity.Info);
    private void ShowInfo(string message) => ShowToast(message, ToastKind.Info, NotificationSeverity.Info);
    private void ShowError(string message) => ShowToast(message, ToastKind.Error, NotificationSeverity.Error);

    public void Dispose()
    {
        StopDeviceStream();
        _feedbackCts?.Cancel();
        _feedbackCts?.Dispose();
    }
}
