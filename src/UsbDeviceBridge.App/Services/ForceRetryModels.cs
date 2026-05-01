namespace UsbDeviceBridge.App.Services;

public enum ForceRetryStage
{
    Bind,
    Attach,
}

public sealed record ForceRetryRequest(
    string DeviceDescription,
    string InstanceId,
    string BusId,
    string WslDistro,
    ForceRetryStage Stage,
    bool IsAutoAttach);

public sealed record ForceRetryDecision(bool RetryWithForce);
