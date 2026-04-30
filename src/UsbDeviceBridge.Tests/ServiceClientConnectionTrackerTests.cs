using UsbDeviceBridge.Service.Domain;

namespace UsbDeviceBridge.Tests;

public class ServiceClientConnectionTrackerTests
{
    [Fact]
    public void HasConnectedClients_ReflectsActiveStreamCount()
    {
        var tracker = new ServiceClientConnectionTracker();

        Assert.False(tracker.HasConnectedClients);
        Assert.Equal(0, tracker.ActiveStreamClients);

        tracker.OnStreamConnected();
        tracker.OnStreamConnected();

        Assert.True(tracker.HasConnectedClients);
        Assert.Equal(2, tracker.ActiveStreamClients);

        tracker.OnStreamDisconnected();

        Assert.True(tracker.HasConnectedClients);
        Assert.Equal(1, tracker.ActiveStreamClients);

        tracker.OnStreamDisconnected();

        Assert.False(tracker.HasConnectedClients);
        Assert.Equal(0, tracker.ActiveStreamClients);
    }

    [Fact]
    public void OnStreamDisconnected_DoesNotGoNegative()
    {
        var tracker = new ServiceClientConnectionTracker();

        tracker.OnStreamDisconnected();
        tracker.OnStreamDisconnected();

        Assert.False(tracker.HasConnectedClients);
        Assert.Equal(0, tracker.ActiveStreamClients);
    }
}
