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

            var hostIp = ResolveHostIpForSshClient();
            if (string.IsNullOrWhiteSpace(hostIp))
            {
                return (false,
                    "Could not determine a reachable host IP for SSH client attach. "
                    + "Ensure networking is available and retry.");
            }

            // Establish a persistent reverse SSH tunnel (ssh -N -R) so that
            // 127.0.0.1:3240 on the remote machine forwards to this host's usbipd port.
            // This process must stay alive while the device is attached; it is kept by
            // SshPortForwardingManager and disposed when the app exits or the device detaches.
            var tunnelResult = await _sshPortForwardingManager.EnsureReverseTunnelAsync(
                sshTarget, hostIp, 3240, ct);
            if (!tunnelResult.Ok)
                return (false, $"Could not establish SSH reverse tunnel to '{sshTarget}': {tunnelResult.Message}");

            return await _usbIpdClient.AttachViaSshClientAsync(sshTarget, busId, ct);
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

    private static async Task<(bool IsReachable, string Message)> ValidateAdHocHostReachabilityAsync(
        string host,
        CancellationToken ct)
    {
        var normalized = host.Trim();
        var hostname = normalized;
        var portSeparator = normalized.LastIndexOf(':');
        if (portSeparator > 0)
            hostname = normalized[..portSeparator];

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

    private static string ResolveHostIpForSshClient()
    {
        try
        {
            // Uses route selection to pick the primary outbound local interface address.
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Connect("8.8.8.8", 53);
            if (udp.Client.LocalEndPoint is IPEndPoint endpoint
                && endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !IPAddress.IsLoopback(endpoint.Address))
            {
                return endpoint.Address.ToString();
            }
        }
        catch
        {
            // Fall through to hostname-based lookup.
        }

        try
        {
            var hostName = Dns.GetHostName();
            var addresses = Dns.GetHostAddresses(hostName)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .ToArray();
            return addresses.FirstOrDefault()?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    public async Task<(bool Ok, string Message)> DetachAsync(string busId, CancellationToken ct)
    {
        try
        {
            return await _usbIpdClient.DetachAsync(busId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detach failed for {BusId}", busId);
            throw;
        }
    }
}

