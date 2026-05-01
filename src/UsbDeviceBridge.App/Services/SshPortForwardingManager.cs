using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.App.Services;

public sealed class SshPortForwardingManager : IDisposable
{
    private readonly Dictionary<string, SshTunnelHandle> _tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SshTunnelHandle> _reverseTunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, int, CancellationToken, Task<(bool Ok, int LocalPort, string Message)>> _startTunnelAsync;

    public SshPortForwardingManager(Func<string, int, CancellationToken, Task<(bool Ok, int LocalPort, string Message)>>? startTunnelAsync = null)
    {
        _startTunnelAsync = startTunnelAsync ?? StartTunnelProcessAsync;
    }

    public async Task<(bool Ok, string Endpoint, string Message)> ResolveAttachEndpointAsync(
        string host,
        string mode,
        CancellationToken ct)
    {
        if (!string.Equals(SshPortForwardModes.Normalize(mode), SshPortForwardModes.Enabled, StringComparison.OrdinalIgnoreCase))
            return (true, host, string.Empty);

        if (_tunnels.TryGetValue(host, out var existing))
        {
            if (!existing.Process.HasExited)
                return (true, $"127.0.0.1:{existing.LocalPort}", string.Empty);

            TryDisposeTunnel(existing);
            _tunnels.Remove(host);
        }

        var (ok, localPort, message) = await _startTunnelAsync(host, 3240, ct);
        if (!ok)
            return (false, string.Empty, message);

        return (true, $"127.0.0.1:{localPort}", string.Empty);
    }

    private async Task<(bool Ok, int LocalPort, string Message)> StartTunnelProcessAsync(
        string host,
        int remotePort,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(host))
            return (false, 0, "SSH host is required for forwarding.");

        var localPort = ReservePort();
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add($"{localPort}:127.0.0.1:{remotePort}");
        psi.ArgumentList.Add(host);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return (false, 0, $"Failed to start ssh port forwarding: {ex.Message}");
        }

        if (process is null)
            return (false, 0, "Failed to start ssh port forwarding process.");

        // Give ssh a brief moment to fail fast on invalid host/key/auth issues.
        await Task.Delay(250, ct);

        if (process.HasExited)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            process.Dispose();
            return (false, 0, string.IsNullOrWhiteSpace(stderr)
                ? "SSH forwarding process exited before tunnel was established."
                : $"SSH forwarding failed: {stderr.Trim()}");
        }

        _tunnels[host] = new SshTunnelHandle(localPort, process);
        return (true, localPort, string.Empty);
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var localPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return localPort;
    }

    private static void TryDisposeTunnel(SshTunnelHandle handle)
    {
        try
        {
            if (!handle.Process.HasExited)
                handle.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort process cleanup.
        }

        handle.Process.Dispose();
    }

    public void Dispose()
    {
        foreach (var tunnel in _tunnels.Values)
            TryDisposeTunnel(tunnel);
        _tunnels.Clear();

        foreach (var tunnel in _reverseTunnels.Values)
            TryDisposeTunnel(tunnel);
        _reverseTunnels.Clear();
    }

    /// <summary>
    /// Ensures a persistent reverse SSH tunnel is running for <paramref name="host"/>,
    /// making <c>127.0.0.1:<paramref name="targetPort"/></c> on the remote machine forward
    /// back to <c><paramref name="targetHost"/>:<paramref name="targetPort"/></c> on Windows.
    /// The process is kept alive until <see cref="Dispose"/> or the next call detects it has exited.
    /// </summary>
    public async Task<(bool Ok, string Message)> EnsureReverseTunnelAsync(
        string host,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
        if (_reverseTunnels.TryGetValue(host, out var existing))
        {
            if (!existing.Process.HasExited)
                return (true, string.Empty);

            TryDisposeTunnel(existing);
            _reverseTunnels.Remove(host);
        }

        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ExitOnForwardFailure=yes");
        psi.ArgumentList.Add("-R");
        psi.ArgumentList.Add($"127.0.0.1:{targetPort}:{targetHost}:{targetPort}");
        psi.ArgumentList.Add(host);

        Process? process;
        try
        {
            process = Process.Start(psi);
        }
        catch (Exception ex)
        {
            return (false, $"Failed to start SSH reverse tunnel: {ex.Message}");
        }

        if (process is null)
            return (false, "Failed to start SSH reverse tunnel process.");

        // Give SSH a moment to authenticate and establish the reverse port binding,
        // or to fail fast on auth/host errors.
        await Task.Delay(800, ct);

        if (process.HasExited)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            process.Dispose();
            return (false, string.IsNullOrWhiteSpace(stderr)
                ? "SSH reverse tunnel process exited before the tunnel was established."
                : $"SSH reverse tunnel failed: {stderr.Trim()}");
        }

        _reverseTunnels[host] = new SshTunnelHandle(targetPort, process);
        return (true, string.Empty);
    }

    private sealed record SshTunnelHandle(int LocalPort, Process Process);
}
