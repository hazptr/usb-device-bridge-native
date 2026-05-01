using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using Usbdevicebridge.V1;

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
    public AttachTargetType PreferredTargetType { get; init; } = AttachTargetType.Wsl;
    public string PreferredTargetName { get; init; } = "";
    public IReadOnlyList<string> AvailableClients { get; init; } = [];
    // ── Mutable: updated at runtime by LocalAutoAttachManager ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel))]
    [NotifyPropertyChangedFor(nameof(BusyVisibility))]
    [NotifyPropertyChangedFor(nameof(ActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanSelectDistro))]
    [NotifyPropertyChangedFor(nameof(CanSelectClient))]
    private bool _isAttaching;

    // ── Mutable: busy flag set by MainViewModel during operations ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyVisibility))]
    [NotifyPropertyChangedFor(nameof(ActionsVisibility))]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [NotifyPropertyChangedFor(nameof(CanSelectDistro))]
    [NotifyPropertyChangedFor(nameof(CanSelectClient))]
    private bool _isBusy;

    // ── Mutable: user's distro selection in the dropdown ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
    private string _selectedDistro = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
    private string _selectedClient = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanSelectDistro))]
    [NotifyPropertyChangedFor(nameof(CanSelectSshHost))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
    [NotifyPropertyChangedFor(nameof(IsWslTarget))]
    [NotifyPropertyChangedFor(nameof(IsSshTarget))]
    [NotifyPropertyChangedFor(nameof(SelectedTargetTypeLabel))]
    private AttachTargetType _selectedTargetType = AttachTargetType.Wsl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
    private string _selectedSshHost = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(ConnectTooltip))]
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
        && HasValidTargetSelection;
    public bool CanDisconnect => State is "attached"  or "shared" && !Remembered && !IsBusy && !IsAttaching;
    public bool CanSelectClient => HasInstanceId
        && !IsBusy
        && !IsAttaching
        && !Remembered
        && State is not ("attached" or "shared");
    public bool CanSelectDistro => !IsBusy
        && !IsAttaching
        && HasInstanceId
        && DistroListReady
        && SelectedTargetType == AttachTargetType.Wsl;
    public bool CanSelectSshHost => !IsBusy
        && !IsAttaching
        && HasInstanceId
        && SelectedTargetType == AttachTargetType.Ssh;

    public Visibility BusyVisibility    => (IsBusy || IsAttaching) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ActionsVisibility => (IsBusy || IsAttaching) ? Visibility.Collapsed : Visibility.Visible;

    public string ConnectTooltip => SelectedTargetType == AttachTargetType.Ssh
        ? "Bind and attach device to the selected SSH target"
        : "Bind and attach device to the selected WSL distro";

    public string RememberTooltip => Remembered
        ? "Forget — stop keeping this device attached automatically"
        : "Remember — keep this device attached to the selected target while the service is running";

    public IReadOnlyList<string> TargetTypeOptions { get; } = ["WSL", "SSH"];

    public string SelectedTargetTypeLabel
    {
        get => SelectedTargetType == AttachTargetType.Ssh ? "SSH" : "WSL";
        set => SelectedTargetType = string.Equals(value, "SSH", StringComparison.OrdinalIgnoreCase)
            ? AttachTargetType.Ssh
            : AttachTargetType.Wsl;
    }

    public bool IsWslTarget
    {
        get => SelectedTargetType == AttachTargetType.Wsl;
        set
        {
            if (value)
                SelectedTargetType = AttachTargetType.Wsl;
        }
    }

    public bool IsSshTarget
    {
        get => SelectedTargetType == AttachTargetType.Ssh;
        set
        {
            if (value)
                SelectedTargetType = AttachTargetType.Ssh;
        }
    }

    public AttachTarget SelectedTarget => SelectedTargetType == AttachTargetType.Ssh
        ? new AttachTarget { Type = AttachTargetType.Ssh, Name = (SelectedSshHost ?? string.Empty).Trim() }
        : new AttachTarget { Type = AttachTargetType.Wsl, Name = (SelectedDistro ?? string.Empty).Trim() };

    partial void OnSelectedClientChanged(string value)
    {
        var target = ParseClientOption(value);
        SelectedTargetType = target.Type;
        if (target.Type == AttachTargetType.Ssh)
            SelectedSshHost = target.Name;
        else
            SelectedDistro = target.Name;
    }

    private static AttachTarget ParseClientOption(string? value)
    {
        var raw = (value ?? string.Empty).Trim();
        if (raw.StartsWith("SSH | ", StringComparison.OrdinalIgnoreCase))
            return new AttachTarget { Type = AttachTargetType.Ssh, Name = raw[6..].Trim() };

        if (raw.StartsWith("WSL | ", StringComparison.OrdinalIgnoreCase))
            return new AttachTarget { Type = AttachTargetType.Wsl, Name = raw[6..].Trim() };

        return new AttachTarget { Type = AttachTargetType.Wsl, Name = string.Empty };
    }

    private bool HasValidTargetSelection => SelectedTargetType switch
    {
        AttachTargetType.Ssh => !string.IsNullOrWhiteSpace(SelectedSshHost),
        _ => !string.IsNullOrWhiteSpace(SelectedDistro),
    };
}

