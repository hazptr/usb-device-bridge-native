using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// The killable half of a tunnel. Abstracted so tunnel lifetime accounting can be
/// tested without spawning real <c>ssh</c> processes.
/// </summary>
public interface ITunnelProcess : IDisposable
{
    bool HasExited { get; }

    /// <summary>OS process id.</summary>
    int Id { get; }

    /// <summary>
    /// Process start time, used together with <see cref="Id"/> to identify this process
    /// across app restarts. A pid alone can be reused by an unrelated process.
    /// </summary>
    DateTime StartTimeUtc { get; }

    void Kill();
}

public sealed class SshPortForwardingManager : IDisposable
{
    private readonly Dictionary<string, SshTunnelHandle> _tunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SshTunnelHandle> _reverseTunnels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, int, CancellationToken, Task<(bool Ok, int LocalPort, string Message)>> _startTunnelAsync;
    private readonly Func<string, string, int, CancellationToken, Task<(bool Ok, ITunnelProcess? Process, string Message)>> _startReverseTunnelAsync;

    // Reverse tunnels are shared between devices and torn down by reference count, so
    // setup and release must not interleave: two devices attaching to the same host
    // concurrently would otherwise each start a tunnel and leak one of them.
    private readonly SemaphoreSlim _reverseGate = new(1, 1);
    private readonly SshTunnelRegistry? _tunnelRegistry;

    public SshPortForwardingManager(
        Func<string, int, CancellationToken, Task<(bool Ok, int LocalPort, string Message)>>? startTunnelAsync = null,
        Func<string, string, int, CancellationToken, Task<(bool Ok, ITunnelProcess? Process, string Message)>>? startReverseTunnelAsync = null,
        SshTunnelRegistry? tunnelRegistry = null)
    {
        _startTunnelAsync = startTunnelAsync ?? StartTunnelProcessAsync;
        _startReverseTunnelAsync = startReverseTunnelAsync ?? StartReverseTunnelProcessAsync;
        _tunnelRegistry = tunnelRegistry;
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
        // -N already suppresses a config RemoteCommand, but an alias with RequestTTY=yes
        // would still allocate a pty for a tunnel that has no use for one.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("RemoteCommand=none");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("RequestTTY=no");
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

        _tunnels[host] = new SshTunnelHandle(localPort, new SshTunnelProcess(process));
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

    private void TryDisposeTunnel(SshTunnelHandle handle)
    {
        try
        {
            if (!handle.Process.HasExited)
                handle.Process.Kill();
        }
        catch
        {
            // Best effort process cleanup.
        }

        // Drop it from the orphan registry first: once disposed the process is no longer
        // this app's problem, and a stale entry would make the next run chase a dead pid.
        _tunnelRegistry?.Forget(handle.Process.Id);
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

        _reverseGate.Dispose();
    }

    /// <summary>
    /// Ensures a persistent reverse SSH tunnel is running for <paramref name="host"/>,
    /// making <c>127.0.0.1:<paramref name="targetPort"/></c> on the remote machine forward
    /// back to <c><paramref name="targetHost"/>:<paramref name="targetPort"/></c> on Windows.
    /// </summary>
    /// <param name="usageKey">
    /// Identifies the caller relying on this tunnel, normally the bus id being attached.
    /// One tunnel serves every device attached to the same host; it stays up until the
    /// last usage key is released via <see cref="ReleaseReverseTunnelAsync"/>.
    /// </param>
    public async Task<(bool Ok, string Message)> EnsureReverseTunnelAsync(
        string host,
        string targetHost,
        int targetPort,
        string usageKey,
        CancellationToken ct)
    {
        await _reverseGate.WaitAsync(ct);
        try
        {
            if (_reverseTunnels.TryGetValue(host, out var existing))
            {
                if (!existing.Process.HasExited)
                {
                    existing.Users.Add(usageKey);
                    return (true, string.Empty);
                }

                TryDisposeTunnel(existing);
                _reverseTunnels.Remove(host);
            }

            var (ok, process, message) = await _startReverseTunnelAsync(host, targetHost, targetPort, ct);
            if (!ok || process is null)
            {
                process?.Dispose();
                return (false, string.IsNullOrWhiteSpace(message)
                    ? "Failed to start SSH reverse tunnel process."
                    : message);
            }

            var handle = new SshTunnelHandle(targetPort, process);
            handle.Users.Add(usageKey);
            _reverseTunnels[host] = handle;
            _tunnelRegistry?.Record(process.Id, process.StartTimeUtc, host);
            return (true, string.Empty);
        }
        finally
        {
            _reverseGate.Release();
        }
    }

    /// <summary>
    /// Drops <paramref name="usageKey"/>'s claim on the reverse tunnel for
    /// <paramref name="host"/> and tears the tunnel down once nothing needs it, so that
    /// detaching a device does not leave an <c>ssh -N</c> process running for the rest of
    /// the app's lifetime.
    /// </summary>
    /// <returns>True when this release closed the tunnel.</returns>
    public async Task<bool> ReleaseReverseTunnelAsync(string host, string usageKey)
    {
        // Deliberately not cancellable: this runs on cleanup paths where abandoning the
        // release would leak the tunnel process.
        await _reverseGate.WaitAsync();
        try
        {
            if (!_reverseTunnels.TryGetValue(host, out var handle))
                return false;

            handle.Users.Remove(usageKey);

            if (handle.Users.Count > 0 && !handle.Process.HasExited)
                return false;

            TryDisposeTunnel(handle);
            _reverseTunnels.Remove(host);
            return true;
        }
        finally
        {
            _reverseGate.Release();
        }
    }

    /// <summary>Test/diagnostic hook: hosts with a live reverse tunnel.</summary>
    internal IReadOnlyCollection<string> ActiveReverseTunnelHosts
    {
        get
        {
            _reverseGate.Wait();
            try
            {
                return _reverseTunnels.Keys.ToArray();
            }
            finally
            {
                _reverseGate.Release();
            }
        }
    }

    private static async Task<(bool Ok, ITunnelProcess? Process, string Message)> StartReverseTunnelProcessAsync(
        string host,
        string targetHost,
        int targetPort,
        CancellationToken ct)
    {
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
        // See StartTunnelProcessAsync: keep interactive aliases usable as tunnel hosts.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("RemoteCommand=none");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("RequestTTY=no");
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
            return (false, null, $"Failed to start SSH reverse tunnel: {ex.Message}");
        }

        if (process is null)
            return (false, null, "Failed to start SSH reverse tunnel process.");

        // Give SSH a moment to authenticate and establish the reverse port binding,
        // or to fail fast on auth/host errors.
        await Task.Delay(800, ct);

        if (process.HasExited)
        {
            var stderr = await process.StandardError.ReadToEndAsync(ct);
            process.Dispose();
            return (false, null, string.IsNullOrWhiteSpace(stderr)
                ? "SSH reverse tunnel process exited before the tunnel was established."
                : $"SSH reverse tunnel failed: {stderr.Trim()}");
        }

        return (true, new SshTunnelProcess(process), string.Empty);
    }

    private sealed class SshTunnelProcess : ITunnelProcess
    {
        private readonly Process _process;

        public SshTunnelProcess(Process process)
        {
            _process = process;
            Id = process.Id;
            // Captured now: StartTime is unreadable once the process has exited.
            StartTimeUtc = process.StartTime.ToUniversalTime();
        }

        public bool HasExited => _process.HasExited;

        public int Id { get; }

        public DateTime StartTimeUtc { get; }

        public void Kill() => _process.Kill(entireProcessTree: true);

        public void Dispose() => _process.Dispose();
    }

    private sealed class SshTunnelHandle(int localPort, ITunnelProcess process)
    {
        public int LocalPort { get; } = localPort;

        public ITunnelProcess Process { get; } = process;

        /// <summary>Usage keys (bus ids) currently relying on this tunnel.</summary>
        public HashSet<string> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
