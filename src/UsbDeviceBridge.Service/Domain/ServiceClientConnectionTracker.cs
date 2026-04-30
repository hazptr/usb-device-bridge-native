using System.Threading;

namespace UsbDeviceBridge.Service.Domain;

public sealed class ServiceClientConnectionTracker
{
    private int _activeStreamClients;

    public int ActiveStreamClients => Volatile.Read(ref _activeStreamClients);

    public bool HasConnectedClients => ActiveStreamClients > 0;

    public void OnStreamConnected()
    {
        Interlocked.Increment(ref _activeStreamClients);
    }

    public void OnStreamDisconnected()
    {
        var updated = Interlocked.Decrement(ref _activeStreamClients);
        if (updated < 0)
        {
            Interlocked.Exchange(ref _activeStreamClients, 0);
        }
    }
}
