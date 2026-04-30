using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using UsbDeviceBridge.App.Models;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Service for managing notification history and filtering.
/// Stores all notifications for the app session and provides filtering capabilities.
/// </summary>
public sealed partial class NotificationService : ObservableObject
{
    private readonly ObservableCollection<Notification> _notifications = new();

    /// <summary>All notifications stored for the session, in reverse chronological order (most recent first).</summary>
    public IReadOnlyCollection<Notification> AllNotifications => _notifications.AsReadOnly();

    /// <summary>Count of unread error notifications. Used for tray badge display.</summary>
    [ObservableProperty]
    private int _unreadErrorCount;

    /// <summary>Count of all unread notifications (any severity). Used for notification badge display.</summary>
    [ObservableProperty]
    private int _unreadCount;

    /// <summary>Raised whenever a new notification is added, for live panel refresh.</summary>
    public event EventHandler? NotificationAdded;

    /// <summary>
    /// Adds a new notification to the store and notifies UI.
    /// </summary>
    public void AddNotification(string message, NotificationSeverity severity, string source = "")
    {
        var notification = new Notification
        {
            Message = message,
            Severity = severity,
            Source = source,
            IsRead = false
        };

        _notifications.Insert(0, notification); // Most recent first

        UpdateUnreadCounts();
        NotificationAdded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Marks a notification as read and updates unread error count if necessary.
    /// </summary>
    public void MarkAsRead(string notificationId)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
        if (notification is null) return;

        notification.IsRead = true;
        UpdateUnreadCounts();
    }

    /// <summary>
    /// Marks all notifications as read.
    /// </summary>
    public void MarkAllAsRead()
    {
        foreach (var notification in _notifications.Where(n => !n.IsRead))
        {
            notification.IsRead = true;
        }

        UpdateUnreadCounts();
    }

    /// <summary>
    /// Removes a single notification from history.
    /// </summary>
    public void Dismiss(string notificationId)
    {
        var notification = _notifications.FirstOrDefault(n => n.Id == notificationId);
        if (notification is null) return;

        _notifications.Remove(notification);
        UpdateUnreadCounts();
    }

    /// <summary>
    /// Clears all notifications from history.
    /// </summary>
    public void ClearAll()
    {
        _notifications.Clear();
        UpdateUnreadCounts();
    }

    /// <summary>
    /// Gets a filtered view of notifications by severity.
    /// </summary>
    public IEnumerable<Notification> GetFiltered(NotificationSeverity? severity = null)
    {
        if (severity is null)
            return _notifications;

        return _notifications.Where(n => n.Severity == severity);
    }

    /// <summary>
    /// Gets a filtered view of notifications by source.
    /// </summary>
    public IEnumerable<Notification> GetFilteredBySource(string source)
    {
        return _notifications.Where(n => n.Source == source);
    }

    /// <summary>
    /// Recalculates the unread error count and updates the observable property.
    /// </summary>
    private void UpdateUnreadCounts()
    {
        UnreadErrorCount = _notifications.Count(n => n.Severity == NotificationSeverity.Error && !n.IsRead);
        UnreadCount = _notifications.Count(n => !n.IsRead);
    }
}
