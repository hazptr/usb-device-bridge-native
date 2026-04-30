using System;

namespace UsbDeviceBridge.App.Models;

/// <summary>
/// Severity level of a notification.
/// </summary>
public enum NotificationSeverity
{
    /// <summary>Informational message.</summary>
    Info,
    /// <summary>Warning message.</summary>
    Warning,
    /// <summary>Error message.</summary>
    Error
}

/// <summary>
/// Represents a single notification event, stored for history and menu display.
/// </summary>
public sealed class Notification
{
    /// <summary>Unique identifier for this notification.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>UTC timestamp when the notification was created.</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>Severity level of the notification.</summary>
    public NotificationSeverity Severity { get; set; }

    /// <summary>User-facing message text.</summary>
    public string Message { get; set; } = "";

    /// <summary>Source of the notification (e.g., "AttachDevice", "ServiceConnection").</summary>
    public string Source { get; set; } = "";

    /// <summary>Whether this notification has been read/dismissed.</summary>
    public bool IsRead { get; set; }
}
