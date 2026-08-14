using System.Text.RegularExpressions;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Builds and interprets the shell commands run on an SSH attach target.
/// </summary>
/// <remarks>
/// The attach work is deliberately expressed as a single remote script rather than a
/// sequence of SSH round trips: each extra <c>ssh</c> invocation costs another TCP
/// handshake plus authentication, and the retry loop needs to react to failures
/// without paying that cost per attempt.
/// </remarks>
public static partial class RemoteUsbIpCommands
{
    /// <summary>Remote exit code: <c>vhci_hcd</c> is missing and could not be loaded.</summary>
    public const int ExitVhciUnavailable = 90;

    /// <summary>Remote exit code: the device was imported but <c>usbip detach</c> failed.</summary>
    public const int ExitDetachFailed = 91;

    /// <summary>Remote exit code: attach never succeeded after all retries.</summary>
    public const int ExitAttachFailed = 92;

    /// <summary>Remote exit code: attach reported success but the device never enumerated.</summary>
    public const int ExitNotEnumerated = 93;

    /// <summary>Attempts made by the remote retry loop.</summary>
    public const int AttachAttempts = 3;

    /// <summary>
    /// Arguments used for every remote <c>udevadm</c> call.
    /// </summary>
    /// <remarks>
    /// Shared with the sudoers rule Setup writes, and that sharing is load-bearing:
    /// sudoers matches command arguments exactly, so a rule granting
    /// <c>udevadm settle</c> does not authorize <c>udevadm settle --timeout=10</c>.
    /// </remarks>
    public const string UdevSettleArgs = "settle --timeout=10";

    [GeneratedRegex(@"^[0-9]+-[0-9]+(\.[0-9]+)*$")]
    private static partial Regex BusIdPattern();

    /// <summary>
    /// USB bus IDs are interpolated into a remote shell script, so they are validated
    /// against a strict pattern rather than escaped.
    /// </summary>
    public static bool IsValidBusId(string? busId) =>
        !string.IsNullOrWhiteSpace(busId) && BusIdPattern().IsMatch(busId.Trim());

    /// <summary>
    /// Finds the vhci port currently importing <c>$BUSID</c>, or nothing if it is not imported.
    /// </summary>
    /// <remarks>
    /// <c>usbip detach</c> takes a vhci port number rather than a bus id. The lookup tracks
    /// the most recent <c>Port NN:</c> header instead of assuming a fixed line offset from the
    /// <c>usbip://host/busid</c> line, because that offset differs between usbip versions.
    /// The bus id is compared as a literal suffix rather than a regex: awk's <c>-v</c>
    /// assignment eats backslashes, so an escaped <c>.</c> for hub paths like <c>1-2.3</c>
    /// would not survive, and an unescaped one would match any character.
    /// </remarks>
    private const string PortForBusIdLookup =
        "awk -v needle=\"/$BUSID\" '"
        + "$1 == \"Port\" { p = $2; sub(/:/, \"\", p); next } "
        + "{ i = index($0, needle); "
        + "if (i > 0 && substr($0, i + length(needle)) ~ /^[ \\t\\r]*$/) { print p; exit } }'";

    /// <summary>
    /// Builds the remote attach script.
    /// </summary>
    /// <remarks>
    /// It does five things the previous single <c>usbip attach</c> call did not:
    /// loads <c>vhci_hcd</c> when absent (it does not survive a reboot on most
    /// distros), clears a stale import of the same bus id left by an earlier session,
    /// settles udev, retries the documented <c>open vhci_driver</c> failure
    /// that occurs when two attaches race each other, and verifies the device actually
    /// enumerated instead of trusting the exit code.
    /// </remarks>
    public static string BuildAttachScript(string busId, string remoteHost, int remotePort)
    {
        if (!IsValidBusId(busId))
            throw new ArgumentException($"Invalid USB bus id '{busId}'.", nameof(busId));

        var normalizedBusId = busId.Trim();
        var host = remoteHost.Trim();

        return string.Join(
            " ",
            "set -u;",
            $"BUSID='{normalizedBusId}';",
            $"RHOST='{host}';",
            $"RPORT='{remotePort}';",
            // vhci_hcd is not loaded by default on most distros and does not survive a reboot.
            "if ! grep -q '^vhci_hcd ' /proc/modules 2>/dev/null; then",
            $"sudo -n modprobe vhci_hcd || exit {ExitVhciUnavailable};",
            "fi;",
            // Clear a stale import of this bus id before attaching. The app's record of
            // which host owns a device is in-memory, so an app restart or crash leaves the
            // remote holding a dead import that still occupies the vhci port and makes
            // every later attach of this bus id fail. Best effort: if it cannot be cleared
            // the retry loop below reports the real failure.
            $"STALE=$(sudo -n usbip port 2>/dev/null | {PortForBusIdLookup});",
            "if [ -n \"$STALE\" ]; then",
            "sudo -n usbip detach --port \"$STALE\" >/dev/null 2>&1 || true;",
            "sleep 1;",
            "fi;",
            $"sudo -n udevadm {UdevSettleArgs} >/dev/null 2>&1 || true;",
            // ACCEPTED tracks whether usbip itself ever reported success, which is what
            // separates "never got the device" (ExitAttachFailed) from "got it but it
            // never showed up" (ExitNotEnumerated). It must therefore only be set inside
            // the success branch below, not once per iteration.
            "ATTACHED=0; ACCEPTED=0;",
            $"for attempt in $(seq 1 {AttachAttempts}); do",
            "if sudo -n usbip attach --remote \"$RHOST\" --busid \"$BUSID\"; then",
            "ACCEPTED=1;",
            // Exit 0 from usbip only means the import request was accepted; the device
            // can still fail to enumerate, so poll for it before declaring success.
            "for i in 1 2 3 4 5; do",
            "sleep 1;",
            "if sudo -n usbip port 2>/dev/null | grep -qE \"usbip://[^/]+/${BUSID}([[:space:]]|$)\"; then",
            "ATTACHED=1; break;",
            "fi;",
            "done;",
            "fi;",
            "if [ \"$ATTACHED\" = \"1\" ]; then break; fi;",
            "sleep 2;",
            "done;",
            "if [ \"$ATTACHED\" = \"1\" ]; then",
            $"sudo -n udevadm {UdevSettleArgs} >/dev/null 2>&1 || true;",
            "exit 0;",
            "fi;",
            $"if [ \"$ACCEPTED\" = \"1\" ]; then exit {ExitNotEnumerated}; fi;",
            $"exit {ExitAttachFailed}"
        );
    }

    /// <summary>
    /// Builds the remote detach script.
    /// </summary>
    /// <remarks>
    /// Uses <see cref="PortForBusIdLookup"/> to turn the bus id into the vhci port
    /// <c>usbip detach</c> requires. The script is idempotent: a device that is not
    /// imported is already detached.
    /// </remarks>
    public static string BuildDetachScript(string busId)
    {
        if (!IsValidBusId(busId))
            throw new ArgumentException($"Invalid USB bus id '{busId}'.", nameof(busId));

        var normalizedBusId = busId.Trim();

        return string.Join(
            " ",
            "set -u;",
            $"BUSID='{normalizedBusId}';",
            // No vhci_hcd means nothing can be imported, so there is nothing to detach.
            "if ! grep -q '^vhci_hcd ' /proc/modules 2>/dev/null; then exit 0; fi;",
            $"PORT=$(sudo -n usbip port 2>/dev/null | {PortForBusIdLookup});",
            "if [ -z \"$PORT\" ]; then exit 0; fi;",
            $"sudo -n usbip detach --port \"$PORT\" || exit {ExitDetachFailed};",
            "exit 0"
        );
    }

    /// <summary>
    /// True when remote output indicates sudo wanted a password, meaning the NOPASSWD
    /// sudoers rule is missing or does not cover the command that was run.
    /// </summary>
    public static bool IndicatesSudoPasswordRequired(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        return output.Contains("password is required", StringComparison.OrdinalIgnoreCase)
            || output.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no tty present", StringComparison.OrdinalIgnoreCase)
            || output.Contains("no password was provided", StringComparison.OrdinalIgnoreCase)
            || output.Contains("may not run", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when the remote reported the vhci driver could not be opened.</summary>
    public static bool IndicatesVhciDriverUnavailable(string? output) =>
        !string.IsNullOrWhiteSpace(output)
        && output.Contains("open vhci_driver", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when the remote could reach usbipd but usbipd did not offer the requested bus id.
    /// </summary>
    /// <remarks>
    /// This is a local problem wearing a remote error: the tunnel worked and usbipd answered,
    /// it simply is not exporting that device. It happens when the device was never shared, or
    /// was shared without releasing it from the Windows driver that still owns it.
    /// </remarks>
    public static bool IndicatesDeviceNotExported(string? output) =>
        !string.IsNullOrWhiteSpace(output)
        && output.Contains("Device not found", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Turns a remote exit code plus its output into an actionable, user-facing message.
    /// </summary>
    /// <param name="operation">
    /// The remote operation being described, used only for wording the generic fallbacks.
    /// </param>
    public static string DescribeFailure(
        int exitCode,
        string? output,
        string sshTarget,
        string operation = "attach")
    {
        var detail = (output ?? string.Empty).Trim();

        if (IndicatesSudoPasswordRequired(detail))
        {
            return $"SSH client '{sshTarget}' requires passwordless sudo for usbip, modprobe, and udevadm. "
                + "Run Setup for this client in Settings to configure it.";
        }

        // Checked ahead of the exit code: this surfaces as ExitNotEnumerated, whose message
        // blames the tunnel, when the tunnel is demonstrably fine and the real cause is local.
        if (IndicatesDeviceNotExported(detail))
        {
            return "usbipd on this PC is not sharing this device, so it could not be imported "
                + $"by '{sshTarget}'. If the device already shows as shared, its Windows driver "
                + "still owns it — retry with a forced bind to release it."
                + FormatDetail(detail);
        }

        return exitCode switch
        {
            ExitDetachFailed =>
                $"usbip detach failed on '{sshTarget}'. The device may still be claimed there."
                + FormatDetail(detail),

            ExitVhciUnavailable =>
                $"The vhci_hcd kernel module could not be loaded on '{sshTarget}'. "
                + "Run Setup for this client in Settings, or install the usbip kernel modules "
                + "(for example linux-tools-generic / kernel-modules-extra) on that machine."
                + FormatDetail(detail),

            ExitNotEnumerated =>
                $"usbip accepted the device on '{sshTarget}' but it never appeared as a USB device. "
                + "The reverse tunnel may have dropped, or the device was reclaimed on this machine."
                + FormatDetail(detail),

            ExitAttachFailed =>
                $"usbip attach failed on '{sshTarget}' after {AttachAttempts} attempts."
                + FormatDetail(detail),

            _ => string.IsNullOrWhiteSpace(detail)
                ? $"SSH client {operation} to '{sshTarget}' failed with exit code {exitCode}."
                : detail,
        };
    }

    private static string FormatDetail(string detail) =>
        string.IsNullOrWhiteSpace(detail) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{detail}";
}
