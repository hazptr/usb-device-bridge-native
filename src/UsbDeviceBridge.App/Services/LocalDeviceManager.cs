using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using UsbDeviceBridge.App.Models;
using UsbDeviceBridge.App.Settings;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Non-elevated device discovery and state management running in user context.
/// Replaces service-side device RPC endpoints (BUG-0006 fix).
/// </summary>
public sealed class LocalDeviceManager
{
    private readonly UsbIpdClient _usbIpdClient;
    private readonly WslUserSpaceInterop _wslUserSpaceInterop;
    private readonly SshConfigParser _sshConfigParser;
    private readonly SshPortForwardingManager _sshPortForwardingManager;
    private readonly Func<string> _getSshPortForwardMode;
    private readonly ILogger<LocalDeviceManager> _logger;

    // Bus id -> SSH host it is currently attached to. Detach only receives a bus id, so
    // this is the only record of which client owns the device and therefore which remote
    // import and reverse tunnel have to be cleaned up.
    private readonly Dictionary<string, string> _sshAttachments = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sshAttachmentsLock = new();

    public LocalDeviceManager(
        UsbIpdClient usbIpdClient,
        WslUserSpaceInterop? wslUserSpaceInterop = null,
        SshConfigParser? sshConfigParser = null,
        SshPortForwardingManager? sshPortForwardingManager = null,
        Func<string>? getSshPortForwardMode = null,
        ILogger<LocalDeviceManager>? logger = null)
    {
        _usbIpdClient = usbIpdClient;
        _wslUserSpaceInterop = wslUserSpaceInterop ?? new WslUserSpaceInterop();
        _sshConfigParser = sshConfigParser ?? new SshConfigParser();
        _sshPortForwardingManager = sshPortForwardingManager ?? new SshPortForwardingManager();
        _getSshPortForwardMode = getSshPortForwardMode ?? (() => SshPortForwardModes.Enabled);
        _logger = logger ?? NullLogger<LocalDeviceManager>.Instance;
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken ct)
    {
        try
        {
            var rawDevices = await _usbIpdClient.GetDevicesAsync(ct);
            var devices = new List<Device>();

            foreach (var raw in rawDevices)
            {
                var state = AppUsbIpdStateParser.Classify(raw);
                devices.Add(new Device
                {
                    InstanceId = raw.InstanceId ?? "",
                    BusId = raw.BusId ?? "",
                    Description = raw.Description ?? "",
                    HardwareId = AppUsbIpdStateParser.ExtractVidPid(raw.InstanceId) ?? "",
                    State = state.ToString().ToLowerInvariant(),
                    Remembered = false,
                    PreferredDistro = "",
                    Attaching = false,
                    Target = new AttachTarget { Type = AttachTargetType.Wsl, Name = string.Empty },
                });
            }

            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDevices failed");
            throw;
        }
    }

    public async Task<bool> HasAnyRunningWslDistroAsync(CancellationToken ct = default)
    {
        var distros = await _wslUserSpaceInterop.QueryDistrosAsync(ct);
        return distros.Any(d => d.IsRunning);
    }

    /// <summary>
    /// Attaches device to an explicit target in user context.
    /// Caller must bind first if device is Available.
    /// Returns the raw usbipd output so the caller can detect firewall blocks.
    /// </summary>
    public async Task<(bool Ok, string Message)> AttachAsync(
        string busId,
        string wslDistro,
        CancellationToken ct)
        => await AttachAsync(
            busId,
            new AttachTarget { Type = AttachTargetType.Wsl, Name = wslDistro ?? string.Empty },
            ct);

    public async Task<(bool Ok, string Message)> AttachAsync(
        string busId,
        AttachTarget target,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(busId))
                return (false, "Bus ID is required.");

            var normalized = NormalizeTarget(target);
            if (normalized.Type == AttachTargetType.Wsl)
            {
                var targetName = normalized.Name;
                if (!string.IsNullOrWhiteSpace(targetName))
                {
                    var distros = await _wslUserSpaceInterop.QueryDistrosAsync(ct);
                    var matched = distros.FirstOrDefault(d =>
                        string.Equals(d.Name, targetName, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrWhiteSpace(matched.Name))
                        return (false, $"WSL target '{targetName}' is not available.");

                    if (!matched.IsRunning)
                        return (false, $"WSL target '{targetName}' is not running.");
                }

                return await _usbIpdClient.AttachAsync(normalized, busId, ct);
            }

            var sshTarget = normalized.Name;
            if (!SshConfigParser.IsValidAdHocHost(sshTarget))
                return (false, $"SSH target '{sshTarget}' has an invalid host format.");

            var knownHosts = _sshConfigParser.GetHostAliases();
            var isKnownConfigHost = knownHosts.Contains(sshTarget, StringComparer.OrdinalIgnoreCase);
            if (knownHosts.Count > 0
                && !isKnownConfigHost
                && sshTarget.Contains('*', StringComparison.Ordinal))
            {
                return (false, "SSH wildcard hosts are not supported for attach targets.");
            }

            if (!isKnownConfigHost)
            {
                var (reachable, reachabilityMessage) = await ValidateAdHocHostReachabilityAsync(sshTarget, ct);
                if (!reachable)
                    return (false, reachabilityMessage);
            }

            var mode = _getSshPortForwardMode();
            if (!string.Equals(mode, SshPortForwardModes.Enabled, StringComparison.OrdinalIgnoreCase))
            {
                return (false,
                    "Automatic SSH client attach is disabled in settings. "
                    + "Set SSH forwarding mode to 'enabled' to attach via SSH automatically.");
            }

            // Bind the device first
            var bindResult = await _usbIpdClient.BindAsync(busId, ct);
            if (!bindResult.Ok)
                return bindResult;

            // Establish a persistent reverse SSH tunnel (ssh -N -R) so that
            // 127.0.0.1:3240 on the remote machine forwards to this host's usbipd port.
            // This process must stay alive while the device is attached; it is kept by
            // SshPortForwardingManager and disposed when the app exits or the device detaches.
            //
            // The forward destination is resolved by the SSH client (this machine), so
            // loopback is both correct and stable. Using a routed interface address here
            // instead would break whenever DHCP reassigned the address, and could pick a
            // VPN or WSL vEthernet adapter that usbipd is not reachable on.
            var normalizedBusId = busId.Trim();
            var tunnelResult = await _sshPortForwardingManager.EnsureReverseTunnelAsync(
                sshTarget, "127.0.0.1", 3240, normalizedBusId, ct);
            if (!tunnelResult.Ok)
                return (false, $"Could not establish SSH reverse tunnel to '{sshTarget}': {tunnelResult.Message}");

            var attachResult = await _usbIpdClient.AttachViaSshClientAsync(sshTarget, busId, ct);

            if (attachResult.Ok)
            {
                lock (_sshAttachmentsLock)
                    _sshAttachments[normalizedBusId] = sshTarget;

                return attachResult;
            }

            // A failed attach leaves nothing to clean up on detach, so release the tunnel
            // here or a retry loop accumulates one ssh process per failed attempt.
            await _sshPortForwardingManager.ReleaseReverseTunnelAsync(sshTarget, normalizedBusId);
            return attachResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attach failed for {BusId}", busId);
            throw;
        }
    }

    private static AttachTarget NormalizeTarget(AttachTarget target)
    {
        var type = target.Type == AttachTargetType.Ssh
            ? AttachTargetType.Ssh
            : AttachTargetType.Wsl;

        return new AttachTarget
        {
            Type = type,
            Name = (target.Name ?? string.Empty).Trim(),
        };
    }

    /// <summary>
    /// Reduces an ssh target to the part DNS can actually resolve.
    /// </summary>
    /// <remarks>
    /// ssh accepts forms DNS does not: <c>user@host</c>, <c>host:port</c>, and bracketed
    /// IPv6 literals. Resolving the raw target made every <c>user@host</c> client fail with
    /// "could not be resolved" even though ssh itself handled it fine.
    /// </remarks>
    internal static string ExtractResolvableHostname(string sshTarget)
    {
        var value = (sshTarget ?? string.Empty).Trim();

        var userSeparator = value.LastIndexOf('@');
        if (userSeparator >= 0)
            value = value[(userSeparator + 1)..];

        if (value.StartsWith('['))
        {
            var closingBracket = value.IndexOf(']');
            if (closingBracket > 1)
                return value[1..closingBracket];
        }

        // Only strip a trailing :port. A bare IPv6 literal has several colons, and
        // splitting on the last one would corrupt it.
        var portSeparator = value.LastIndexOf(':');
        if (portSeparator > 0
            && value.IndexOf(':') == portSeparator
            && int.TryParse(value[(portSeparator + 1)..], out _))
        {
            value = value[..portSeparator];
        }

        return value;
    }

    private static async Task<(bool IsReachable, string Message)> ValidateAdHocHostReachabilityAsync(
        string host,
        CancellationToken ct)
    {
        var hostname = ExtractResolvableHostname(host);

        if (hostname.Length == 0)
            return (false, $"SSH target '{host}' does not contain a host name.");

        if (string.Equals(hostname, "localhost", StringComparison.OrdinalIgnoreCase)
            || string.Equals(hostname, "127.0.0.1", StringComparison.OrdinalIgnoreCase))
        {
            return (true, string.Empty);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostname, ct);
            if (addresses.Length == 0)
                return (false, $"SSH target '{host}' could not be resolved.");
        }
        catch
        {
            return (false, $"SSH target '{host}' could not be resolved.");
        }

        return (true, string.Empty);
    }

    public async Task<(bool Ok, string Message)> DetachAsync(string busId, CancellationToken ct)
    {
        try
        {
            var normalizedBusId = (busId ?? string.Empty).Trim();

            string? sshTarget;
            lock (_sshAttachmentsLock)
                _sshAttachments.TryGetValue(normalizedBusId, out sshTarget);

            if (string.IsNullOrEmpty(sshTarget))
                return await _usbIpdClient.DetachAsync(normalizedBusId, ct);

            // Release the client's vhci port first, while the tunnel it travels over is
            // still up. Doing this after the local detach would leave the remote holding a
            // dead import, which occupies the port and breaks the next attach of this bus id.
            var remoteDetach = await _usbIpdClient.DetachViaSshClientAsync(sshTarget, normalizedBusId, ct);
            if (!remoteDetach.Ok)
            {
                // Best effort only: a client we cannot reach must not block the local
                // detach, or the device stays bound here with no way to release it.
                _logger.LogWarning(
                    "Remote usbip detach on '{SshTarget}' failed for {BusId}: {Message}",
                    sshTarget, normalizedBusId, remoteDetach.Message);
            }

            try
            {
                return await _usbIpdClient.DetachAsync(normalizedBusId, ct);
            }
            finally
            {
                lock (_sshAttachmentsLock)
                    _sshAttachments.Remove(normalizedBusId);

                await _sshPortForwardingManager.ReleaseReverseTunnelAsync(sshTarget, normalizedBusId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detach failed for {BusId}", busId);
            throw;
        }
    }
}

