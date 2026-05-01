using System.Collections.Concurrent;

namespace UsbDeviceBridge.Service.Domain;

/// <summary>
/// A single pending notification emitted by the auto-attach background worker,
/// to be forwarded to connected UI clients via the device stream.
/// </summary>
public sealed record AutoAttachNotification(
    string Message,
    string Severity = "warning",
    string Code = "",
    string InstanceId = "",
    string BusId = "",
    string WslDistro = "");

/// <summary>
/// Thread-safe FIFO store for notifications produced by the auto-attach background
/// service.  <see cref="DeviceServiceImpl"/> drains this store on each stream poll
/// cycle and emits the notifications as <c>DeviceEvent</c> messages with
/// <c>event_type = "notification"</c>.
/// </summary>
public sealed class AutoAttachNotificationStore
{
    private readonly ConcurrentQueue<AutoAttachNotification> _pending = new();

    /// <summary>Enqueues a notification for delivery to connected UI clients.</summary>
    public void Enqueue(
        string message,
        string severity = "warning",
        string code = "",
        string instanceId = "",
        string busId = "",
        string wslDistro = "")
    {
        if (!string.IsNullOrWhiteSpace(message))
            _pending.Enqueue(new AutoAttachNotification(message, severity, code, instanceId, busId, wslDistro));
    }

    /// <summary>
    /// Removes and returns all pending notifications.
    /// Returns an empty list when there is nothing queued.
    /// </summary>
    public IReadOnlyList<AutoAttachNotification> DrainPending()
    {
        if (_pending.IsEmpty)
            return [];

        var results = new List<AutoAttachNotification>();
        while (_pending.TryDequeue(out var item))
            results.Add(item);

        return results;
    }
}
