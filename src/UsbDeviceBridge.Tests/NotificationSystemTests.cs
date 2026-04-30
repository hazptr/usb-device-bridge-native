using Xunit;
using UsbDeviceBridge.App.Models;
using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

/// <summary>
/// Unit tests for the notification system (model and service).
/// </summary>
public sealed class NotificationSystemTests
{
    [Fact]
    public void Notification_ShouldCreateWithCorrectDefaults()
    {
        // Act
        var notification = new Notification
        {
            Message = "Test message",
            Severity = NotificationSeverity.Error,
            Source = "TestSource"
        };

        // Assert
        Assert.NotEmpty(notification.Id);
        Assert.Equal("Test message", notification.Message);
        Assert.Equal(NotificationSeverity.Error, notification.Severity);
        Assert.Equal("TestSource", notification.Source);
        Assert.False(notification.IsRead);
        Assert.True(notification.Timestamp > System.DateTime.MinValue);
    }

    [Fact]
    public void NotificationService_ShouldAddNotifications()
    {
        // Arrange
        var service = new NotificationService();

        // Act
        service.AddNotification("Error 1", NotificationSeverity.Error, "Source1");
        service.AddNotification("Warning 1", NotificationSeverity.Warning, "Source2");

        // Assert
        Assert.Equal(2, ((System.Collections.ICollection)service.AllNotifications).Count);
    }

    [Fact]
    public void NotificationService_ShouldInsertNewestFirst()
    {
        // Arrange
        var service = new NotificationService();

        // Act
        service.AddNotification("Error 1", NotificationSeverity.Error);
        var notification1Id = service.AllNotifications.FirstOrDefault()?.Id;
        
        service.AddNotification("Error 2", NotificationSeverity.Error);
        var notification2Id = service.AllNotifications.FirstOrDefault()?.Id;

        // Assert
        Assert.NotEqual(notification1Id, notification2Id);
        Assert.Equal("Error 2", service.AllNotifications.FirstOrDefault()?.Message);
    }

    [Fact]
    public void NotificationService_ShouldFilterBySeverity()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error", NotificationSeverity.Error);
        service.AddNotification("Warning", NotificationSeverity.Warning);
        service.AddNotification("Info", NotificationSeverity.Info);

        // Act
        var errors = service.GetFiltered(NotificationSeverity.Error).ToList();
        var warnings = service.GetFiltered(NotificationSeverity.Warning).ToList();
        var infos = service.GetFiltered(NotificationSeverity.Info).ToList();

        // Assert
        Assert.Single(errors);
        Assert.Single(warnings);
        Assert.Single(infos);
        Assert.Equal("Error", errors[0].Message);
        Assert.Equal("Warning", warnings[0].Message);
        Assert.Equal("Info", infos[0].Message);
    }

    [Fact]
    public void NotificationService_ShouldTrackUnreadErrorCount()
    {
        // Arrange
        var service = new NotificationService();

        // Act
        service.AddNotification("Error 1", NotificationSeverity.Error);
        service.AddNotification("Error 2", NotificationSeverity.Error);
        service.AddNotification("Warning", NotificationSeverity.Warning);

        // Assert
        Assert.Equal(2, service.UnreadErrorCount);
    }

    [Fact]
    public void NotificationService_ShouldMarkAsRead()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error", NotificationSeverity.Error);
        var errorId = service.AllNotifications.First().Id;

        // Act
        service.MarkAsRead(errorId);

        // Assert
        Assert.Equal(0, service.UnreadErrorCount);
        Assert.True(service.AllNotifications.First().IsRead);
    }

    [Fact]
    public void NotificationService_ShouldMarkAllAsRead()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error 1", NotificationSeverity.Error);
        service.AddNotification("Error 2", NotificationSeverity.Error);
        service.AddNotification("Warning", NotificationSeverity.Warning);

        // Act
        service.MarkAllAsRead();

        // Assert
        Assert.Equal(0, service.UnreadErrorCount);
        Assert.All(service.AllNotifications, n => Assert.True(n.IsRead));
    }

    [Fact]
    public void NotificationService_ShouldDismissNotification()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error 1", NotificationSeverity.Error);
        service.AddNotification("Error 2", NotificationSeverity.Error);
        var errorId = service.AllNotifications.First().Id;

        // Act
        service.Dismiss(errorId);

        // Assert
        Assert.Single(service.AllNotifications);
        Assert.Equal(1, service.UnreadErrorCount);
    }

    [Fact]
    public void NotificationService_ShouldClearAll()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error", NotificationSeverity.Error);
        service.AddNotification("Warning", NotificationSeverity.Warning);

        // Act
        service.ClearAll();

        // Assert
        Assert.Empty(service.AllNotifications);
        Assert.Equal(0, service.UnreadErrorCount);
    }

    [Fact]
    public void NotificationService_GetFiltered_WithNullSeverity_ShouldReturnAll()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error", NotificationSeverity.Error);
        service.AddNotification("Warning", NotificationSeverity.Warning);
        service.AddNotification("Info", NotificationSeverity.Info);

        // Act
        var all = service.GetFiltered(null).ToList();

        // Assert
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public void NotificationService_OnlyErrorsCountAsUnread()
    {
        // Arrange
        var service = new NotificationService();
        service.AddNotification("Error", NotificationSeverity.Error);
        service.AddNotification("Warning", NotificationSeverity.Warning);
        service.AddNotification("Info", NotificationSeverity.Info);

        // Act & Assert
        Assert.Equal(1, service.UnreadErrorCount);
        
        // Mark the error as read
        service.MarkAsRead(service.AllNotifications.First(n => n.Severity == NotificationSeverity.Error).Id);
        Assert.Equal(0, service.UnreadErrorCount);
    }

    [Fact]
    public void NotificationService_DismissNonExistentNotification_ShouldNotThrow()
    {
        // Arrange
        var service = new NotificationService();

        // Act & Assert
        service.Dismiss("nonexistent-id"); // Should not throw
        Assert.Empty(service.AllNotifications);
    }

    [Fact]
    public void NotificationService_MarkNonExistentAsRead_ShouldNotThrow()
    {
        // Arrange
        var service = new NotificationService();

        // Act & Assert
        service.MarkAsRead("nonexistent-id"); // Should not throw
        Assert.Empty(service.AllNotifications);
    }
}
