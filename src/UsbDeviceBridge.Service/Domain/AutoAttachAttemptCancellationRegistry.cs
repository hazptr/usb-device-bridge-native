using System.Collections.Concurrent;

namespace UsbDeviceBridge.Service.Domain;

public sealed class AutoAttachAttemptCancellationRegistry
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inflight =
        new(StringComparer.OrdinalIgnoreCase);

    public bool Register(string instanceId, CancellationTokenSource cancellationTokenSource)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (cancellationTokenSource is null)
            throw new ArgumentNullException(nameof(cancellationTokenSource));

        var normalized = instanceId.Trim();
        if (_inflight.TryGetValue(normalized, out var existing))
        {
            _inflight[normalized] = cancellationTokenSource;
            existing.Cancel();
            existing.Dispose();
            return true;
        }

        return _inflight.TryAdd(normalized, cancellationTokenSource);
    }

    public bool Cancel(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (!_inflight.TryRemove(instanceId.Trim(), out var cts))
            return false;

        cts.Cancel();
        cts.Dispose();
        return true;
    }

    public bool Complete(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        if (!_inflight.TryRemove(instanceId.Trim(), out var cts))
            return false;

        cts.Dispose();
        return true;
    }
}