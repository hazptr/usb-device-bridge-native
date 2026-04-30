namespace UsbDeviceBridge.App.Services;

public readonly record struct ServiceRecoveryText(string Message, string Details);

public static class ServiceRecoveryTextFactory
{
    public static ServiceRecoveryText Reconnecting()
        => new(
            "Reconnecting to the background service...",
            "Device actions are temporarily disabled while we reconnect.");

    public static ServiceRecoveryText ServiceNotRunning()
        => new(
            "It looks like the service is not running.",
            "Device actions are disabled while the app reconnects. Use Restart Service to recover now.");

    public static ServiceRecoveryText LostConnection()
        => new(
            "The app lost connection to the background service.",
            "Device actions are disabled while the app reconnects. Use Restart Service to recover now.");

    public static ServiceRecoveryText RestartInProgress()
        => new(
            "It looks like the service is not running.",
            "Starting the service now. This may take a few seconds.");

    public static ServiceRecoveryText RestartCancelledOrBlocked()
        => new(
            "It looks like the service is not running.",
            "Restart was cancelled or blocked. Click Restart Service to try again.");

    public static ServiceRecoveryText RestartStillUnavailable()
        => new(
            "It looks like the service is not running.",
            "Restart did not reconnect yet. Confirm the service is installed, then try Restart Service again.");

    public static ServiceRecoveryText RestartUnexpectedFailure()
        => new(
            "It looks like the service is not running.",
            "Restart failed unexpectedly. Check service installation and retry.");
}
