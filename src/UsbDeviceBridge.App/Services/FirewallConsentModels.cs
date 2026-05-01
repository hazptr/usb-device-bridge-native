namespace UsbDeviceBridge.App.Services;

public sealed record FirewallConsentRequest(
    string DeviceDescription,
    string InstanceId,
    string BusId,
    string WslDistro,
    bool IsAutoAttach);

public sealed record FirewallConsentDecision(
    bool AllowNow,
    bool RememberChoice);
