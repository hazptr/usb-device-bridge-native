using UsbDeviceBridge.App.Services;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.Tests;

public sealed class SshPortForwardingManagerTests
{
    [Fact]
    public async Task ResolveAttachEndpointAsync_WhenModeDisabled_UsesDirectHost()
    {
        var wasCalled = false;
        var manager = new SshPortForwardingManager((_, _, _) =>
        {
            wasCalled = true;
            return Task.FromResult((true, 4242, string.Empty));
        });

        var (ok, endpoint, message) = await manager.ResolveAttachEndpointAsync("my-host", SshPortForwardModes.Disabled, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("my-host", endpoint);
        Assert.Equal(string.Empty, message);
        Assert.False(wasCalled);
    }

    [Fact]
    public async Task ResolveAttachEndpointAsync_WhenModeEnabled_UsesTunnelEndpoint()
    {
        var manager = new SshPortForwardingManager((_, _, _) =>
            Task.FromResult((true, 54321, string.Empty)));

        var (ok, endpoint, message) = await manager.ResolveAttachEndpointAsync("my-host", SshPortForwardModes.Enabled, CancellationToken.None);

        Assert.True(ok);
        Assert.Equal("127.0.0.1:54321", endpoint);
        Assert.Equal(string.Empty, message);
    }

    [Fact]
    public async Task ResolveAttachEndpointAsync_WhenTunnelSetupFails_ReturnsError()
    {
        var manager = new SshPortForwardingManager((_, _, _) =>
            Task.FromResult((false, 0, "forwarding failed")));

        var (ok, endpoint, message) = await manager.ResolveAttachEndpointAsync("my-host", SshPortForwardModes.Enabled, CancellationToken.None);

        Assert.False(ok);
        Assert.Equal(string.Empty, endpoint);
        Assert.Equal("forwarding failed", message);
    }

    /// <summary>
    /// Stands in for an <c>ssh -N -R</c> process so tunnel lifetime can be asserted.
    /// </summary>
    private sealed class FakeTunnelProcess(int id) : ITunnelProcess
    {
        public bool HasExited { get; private set; }

        public int KillCount { get; private set; }

        public int Id { get; } = id;

        public DateTime StartTimeUtc { get; } = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

        public void Kill()
        {
            KillCount++;
            HasExited = true;
        }

        public void Dispose() => HasExited = true;

        public void SimulateExit() => HasExited = true;
    }

    private static (SshPortForwardingManager Manager, List<FakeTunnelProcess> Started) CreateManager(
        SshTunnelRegistry? registry = null)
    {
        var started = new List<FakeTunnelProcess>();
        var manager = new SshPortForwardingManager(
            startReverseTunnelAsync: (_, _, _, _) =>
            {
                var process = new FakeTunnelProcess(9000 + started.Count);
                started.Add(process);
                return Task.FromResult<(bool, ITunnelProcess?, string)>((true, process, string.Empty));
            },
            tunnelRegistry: registry);

        return (manager, started);
    }

    [Fact]
    public async Task EnsureReverseTunnelAsync_ReusesOneTunnelForMultipleDevices()
    {
        var (manager, started) = CreateManager();

        Assert.True((await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None)).Ok);
        Assert.True((await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-7", CancellationToken.None)).Ok);

        Assert.Single(started);
        Assert.Equal(new[] { "desktop" }, manager.ActiveReverseTunnelHosts);
    }

    [Fact]
    public async Task ReleaseReverseTunnelAsync_KeepsTunnelWhileAnotherDeviceNeedsIt()
    {
        var (manager, started) = CreateManager();
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-7", CancellationToken.None);

        var closed = await manager.ReleaseReverseTunnelAsync("desktop", "1-6");

        Assert.False(closed);
        Assert.Equal(0, started[0].KillCount);
        Assert.False(started[0].HasExited);
        Assert.Equal(new[] { "desktop" }, manager.ActiveReverseTunnelHosts);
    }

    [Fact]
    public async Task ReleaseReverseTunnelAsync_ClosesTunnelWhenLastDeviceDetaches()
    {
        var (manager, started) = CreateManager();
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-7", CancellationToken.None);

        Assert.False(await manager.ReleaseReverseTunnelAsync("desktop", "1-6"));
        Assert.True(await manager.ReleaseReverseTunnelAsync("desktop", "1-7"));

        // The ssh process must not outlive the last attached device.
        Assert.Equal(1, started[0].KillCount);
        Assert.Empty(manager.ActiveReverseTunnelHosts);
    }

    [Fact]
    public async Task ReleaseReverseTunnelAsync_IsIdempotentAndIgnoresUnknownKeys()
    {
        var (manager, _) = CreateManager();
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);

        Assert.False(await manager.ReleaseReverseTunnelAsync("desktop", "9-9"));
        Assert.True(await manager.ReleaseReverseTunnelAsync("desktop", "1-6"));
        Assert.False(await manager.ReleaseReverseTunnelAsync("desktop", "1-6"));
        Assert.False(await manager.ReleaseReverseTunnelAsync("laptop", "1-6"));
    }

    [Fact]
    public async Task EnsureReverseTunnelAsync_KeepsHostsIndependent()
    {
        var (manager, started) = CreateManager();
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);
        await manager.EnsureReverseTunnelAsync("laptop", "127.0.0.1", 3240, "1-6", CancellationToken.None);

        Assert.Equal(2, started.Count);
        Assert.True(await manager.ReleaseReverseTunnelAsync("desktop", "1-6"));

        Assert.Equal(new[] { "laptop" }, manager.ActiveReverseTunnelHosts);
        Assert.Equal(1, started[0].KillCount);
        Assert.Equal(0, started[1].KillCount);
    }

    [Fact]
    public async Task EnsureReverseTunnelAsync_ReplacesATunnelWhoseProcessDied()
    {
        var (manager, started) = CreateManager();
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);

        started[0].SimulateExit();

        Assert.True((await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-7", CancellationToken.None)).Ok);
        Assert.Equal(2, started.Count);

        // The replacement is owned only by the device that created it.
        Assert.True(await manager.ReleaseReverseTunnelAsync("desktop", "1-7"));
        Assert.Empty(manager.ActiveReverseTunnelHosts);
    }

    [Fact]
    public async Task EnsureReverseTunnelAsync_WhenStartFails_RegistersNothing()
    {
        var manager = new SshPortForwardingManager(
            startReverseTunnelAsync: (_, _, _, _) =>
                Task.FromResult<(bool, ITunnelProcess?, string)>((false, null, "permission denied")));

        var (ok, message) = await manager.EnsureReverseTunnelAsync(
            "desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);

        Assert.False(ok);
        Assert.Equal("permission denied", message);
        Assert.Empty(manager.ActiveReverseTunnelHosts);
    }

    [Fact]
    public async Task EnsureReverseTunnelAsync_RecordsTunnelsSoALaterRunCanSweepThem()
    {
        var path = Path.Combine(Path.GetTempPath(), $"udb-tunnels-{Guid.NewGuid():N}.json");
        try
        {
            var registry = new SshTunnelRegistry(path);
            var (manager, started) = CreateManager(registry);

            await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);

            var recorded = registry.ReadRecordsForTest();
            var record = Assert.Single(recorded);
            Assert.Equal(started[0].Id, record.Pid);
            Assert.Equal("desktop", record.Host);

            // Releasing the last user must also clear the record, or the next run chases a dead pid.
            Assert.True(await manager.ReleaseReverseTunnelAsync("desktop", "1-6"));
            Assert.Empty(registry.ReadRecordsForTest());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Dispose_ClearsRecordsForEveryTunnelItKills()
    {
        var path = Path.Combine(Path.GetTempPath(), $"udb-tunnels-{Guid.NewGuid():N}.json");
        try
        {
            var registry = new SshTunnelRegistry(path);
            var (manager, _) = CreateManager(registry);
            await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);
            await manager.EnsureReverseTunnelAsync("laptop", "127.0.0.1", 3240, "2-1", CancellationToken.None);
            Assert.Equal(2, registry.ReadRecordsForTest().Count);

            manager.Dispose();

            Assert.Empty(registry.ReadRecordsForTest());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task Dispose_KillsRemainingReverseTunnels()
    {
        var (manager, started) = CreateManager();
        await manager.EnsureReverseTunnelAsync("desktop", "127.0.0.1", 3240, "1-6", CancellationToken.None);
        await manager.EnsureReverseTunnelAsync("laptop", "127.0.0.1", 3240, "2-1", CancellationToken.None);

        manager.Dispose();

        Assert.All(started, p => Assert.Equal(1, p.KillCount));
    }
}
