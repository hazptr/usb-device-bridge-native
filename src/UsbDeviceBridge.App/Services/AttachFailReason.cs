namespace UsbDeviceBridge.App.Services;

public enum AttachFailReason
{
    Unspecified = 0,
    PolicyPrevented = 1,
    FirewallFixFailed = 2,
    StillFailedAfterFix = 3,
}
