using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace UsbDeviceBridge.App.ViewModels;

public sealed partial class DeviceViewModel : ObservableObject
{
    // ── Immutable identity / state (set once from GetDevices response) ──

    public string InstanceId { get; init; } = "";
    public string BusId { get; init; } = "";
    public string Description { get; init; } = "";
    public string HardwareId { get; init; } = "";
    public string State { get; init; } = "";          // "available" | "shared" | "attached" | "offline"
    public bool Remembered { get; init; }
    public string PreferredDistro { get; init; } = "";
    public bool IsAttaching { get; init; }

    // ── Mutable: busy flag set by MainViewModel during operations ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyVisibility))]
    [NotifyPropertyChangedFor(nameof(ActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanSelectDistro))]
    private bool _isBusy;

    // ── Mutable: user's distro selection in the dropdown ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
    [NotifyPropertyChangedFor(nameof(DistroTooltip))]
    private string _selectedDistro = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
    [NotifyPropertyChangedFor(nameof(DistroTooltip))]
    private bool _selectedDistroIsRunning = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelectDistro))]
    private bool _distroListReady = true;

    // ── Computed display / visibility properties ──

    public string StatusLabel => State switch
    {
        _ when IsAttaching => "Attaching",
        "attached" => "Attached",
        "shared"   => "Shared",
        "offline"  => "Offline",
        _          => "Available",
    };

    public bool HasInstanceId => !string.IsNullOrEmpty(InstanceId);

    // Connect is shown when the device can be connected (available or shared but not attached)
    public Visibility ConnectVisibility =>
        State is "available" or "shared"
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Disconnect shown when device is already consuming a USB/IP slot
    public Visibility DisconnectVisibility =>
        State is "attached" or "shared"
            ? Visibility.Visible
            : Visibility.Collapsed;

    // Remembered devices are managed by auto-attach; manual buttons are disabled
    public bool CanConnect    =>
        State is "available" or "shared"
        && !Remembered
        && !IsBusy
        && !IsAttaching
        && !string.IsNullOrWhiteSpace(SelectedDistro)
        && SelectedDistroIsRunning;
    public bool CanDisconnect => State is "attached"  or "shared" && !Remembered && !IsBusy && !IsAttaching;
    public bool CanSelectDistro => !IsBusy && !IsAttaching && HasInstanceId && DistroListReady;

    public Visibility BusyVisibility    => (IsBusy || IsAttaching) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionsVisibility => (IsBusy || IsAttaching) ? Visibility.Collapsed : Visibility.Visible;

    public string ConnectTooltip => "Bind and attach device to WSL";

    public string DistroTooltip
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SelectedDistro))
                return "Select a distro";

            var state = SelectedDistroIsRunning ? "Running" : "Offline";
            return $"{SelectedDistro} - {state}";
        }
    }

    public string RememberTooltip => Remembered
        ? "Forget — stop keeping this device attached automatically"
        : "Remember — keep this device attached to WSL while the service is running";
}
