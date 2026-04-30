using System.Collections.Concurrent;

namespace UsbDeviceBridge.Service.Domain;

public sealed class AutoAttachActivityTracker
{
    private readonly ConcurrentDictionary<string, byte> _attaching = new();

    public void MarkAttaching(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;
        _attaching[instanceId] = 0;
    }

    public void ClearAttaching(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;
        _attaching.TryRemove(instanceId, out _);
    }

    public bool IsAttaching(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;
        return _attaching.ContainsKey(instanceId);
    }
}
