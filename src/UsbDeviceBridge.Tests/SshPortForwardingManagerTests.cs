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
}
