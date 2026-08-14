namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Centralized, user-actionable toast message text for firewall-recovery outcomes
/// during manual attach and auto-attach operations.
/// All messages are kept here to prevent duplicated copy across view-models and services.
/// </summary>
public static class AttachToastMessages
{
    // ── Manual attach ─────────────────────────────────────────────────────────

    /// <summary>
    /// Policy (ask or never) prevented the service from applying the firewall fix automatically.
    /// </summary>
    public static string PolicyPrevented(string deviceDescription)
        => $"Firewall may be blocking \"{deviceDescription}\". "
         + "Enable 'Auto-fix firewall' in Settings or approve the fix when prompted.";

    public static string PolicyPreventedAutoAttach(string deviceId)
        => $"Auto-attach needs firewall-recovery approval for device {deviceId}. "
         + "Open the prompt or set Firewall recovery policy to Always in Settings.";

    /// <summary>
    /// The service attempted the firewall fix but it failed or did not take effect.
    /// <paramref name="detail"/> carries the reason reported by the service — most usefully
    /// when group policy discards the fix, which no local change can work around.
    /// </summary>
    public static string FirewallFixFailed(string deviceDescription, string? detail = null)
        => string.IsNullOrWhiteSpace(detail)
            ? $"Automatic firewall recovery failed for \"{deviceDescription}\". "
              + "Check Windows Firewall settings or try again."
            : $"Automatic firewall recovery failed for \"{deviceDescription}\": {detail.Trim()}";

    /// <summary>
    /// The firewall fix was applied but usbipd attach still failed on the retry.
    /// </summary>
    public static string StillFailedAfterFix(string deviceDescription)
        => $"Firewall fix applied but \"{deviceDescription}\" still failed to attach. "
         + "Check Windows Firewall and WSL network settings.";

    /// <summary>
    /// The firewall fix was applied and the attach retry succeeded.
    /// </summary>
    public static string FirewallFixAppliedAndSucceeded(string deviceDescription, string distro)
        => string.IsNullOrWhiteSpace(distro)
            ? $"Firewall fix applied — \"{deviceDescription}\" connected."
            : $"Firewall fix applied — \"{deviceDescription}\" connected to WSL | {distro}";

    // ── Auto-attach (notifications from the background service) ──────────────

    /// <summary>
    /// Auto-attach succeeded after applying the firewall fix.
    /// Used when the service emits a success notification event.
    /// </summary>
    public static string AutoAttachFirewallFixApplied(string deviceId)
        => $"Firewall fix applied — device {deviceId} auto-attached after retry.";

    /// <summary>
    /// Auto-attach still failing after the firewall fix was applied.
    /// </summary>
    public static string AutoAttachStillFailedAfterFix(string deviceId)
        => $"Auto-attach still failing for device {deviceId} after firewall fix. "
         + "Check firewall settings.";

    /// <summary>
    /// The firewall fix itself failed during an auto-attach attempt.
    /// </summary>
    public static string AutoAttachFirewallFixFailed(string deviceId)
        => $"Automatic firewall recovery failed during auto-attach for device {deviceId}. "
         + "Check Windows Firewall settings.";

    // ── Busy-device force retry outcomes ──────────────────────────────────────

    public static string ForceRetryRequired(string deviceDescription, ForceRetryStage stage)
        => $"{StageLabel(stage)} for \"{deviceDescription}\" reported a busy device. Retry with --force?";

    public static string ForceRetryCancelled(string deviceDescription, ForceRetryStage stage)
        => $"{StageLabel(stage)} for \"{deviceDescription}\" was not retried with --force.";

    public static string ForceRetrySucceeded(string deviceDescription, ForceRetryStage stage)
        => $"{StageLabel(stage)} for \"{deviceDescription}\" succeeded with --force.";

    public static string ForceRetryFailed(string deviceDescription, ForceRetryStage stage, string details)
        => $"{StageLabel(stage)} force retry failed for \"{deviceDescription}\": {NormalizeDetail(details)}";

    public static string ForceRetryCancelledAutoAttach(string deviceId, ForceRetryStage stage)
        => $"Auto-attach {StageLabel(stage).ToLowerInvariant()} for device {deviceId} was not retried with --force.";

    public static string ForceRetrySucceededAutoAttach(string deviceId, ForceRetryStage stage)
        => $"Auto-attach {StageLabel(stage).ToLowerInvariant()} for device {deviceId} succeeded with --force.";

    public static string ForceRetryFailedAutoAttach(string deviceId, ForceRetryStage stage, string details)
        => $"Auto-attach {StageLabel(stage).ToLowerInvariant()} force retry failed for device {deviceId}: {NormalizeDetail(details)}";

    private static string StageLabel(ForceRetryStage stage)
        => stage == ForceRetryStage.Bind ? "Bind" : "Attach";

    private static string NormalizeDetail(string? detail)
        => string.IsNullOrWhiteSpace(detail) ? "Operation failed." : detail.Trim();
}
