using CommunityToolkit.Mvvm.ComponentModel;

namespace UsbDeviceBridge.App.ViewModels;

public sealed partial class DistroOption : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel))]
    [NotifyPropertyChangedFor(nameof(NameStateTooltip))]
    private bool _isRunning;

    public string Name { get; init; } = string.Empty;

    public string StateLabel => IsRunning ? "Running" : "Offline";

    public string NameStateTooltip => $"{Name} - {StateLabel}";
}