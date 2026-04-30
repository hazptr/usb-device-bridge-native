using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using ItemsControl = System.Windows.Controls.ItemsControl;
using ScrollViewer = System.Windows.Controls.ScrollViewer;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing the notification menu panel logic for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private string _currentNotificationFilter = "All";
    private IEnumerable<Models.Notification> _filteredNotifications = new List<Models.Notification>();

    private Border NotificationMenuBorder => NotificationMenuPanel.MenuBorder;

    private TranslateTransform NotificationMenuTransform => NotificationMenuPanel.MenuTransform;

    private ItemsControl NotificationsItemsControl => NotificationMenuPanel.NotificationsItems;

    private ScrollViewer NotificationsScrollViewer => NotificationMenuPanel.NotificationsScroll;

    private Border NotificationEmptyState => NotificationMenuPanel.EmptyState;

    private Button FilterAllBtn => NotificationMenuPanel.FilterAllButton;

    private Button FilterErrorsBtn => NotificationMenuPanel.FilterErrorsButton;

    private Button FilterWarningsBtn => NotificationMenuPanel.FilterWarningsButton;

    private Button FilterInfoBtn => NotificationMenuPanel.FilterInfoButton;

    private void NotificationMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (NotificationMenuPanel.Visibility == Visibility.Visible)
        {
            NotificationMenuPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _vm.NotificationService.MarkAllAsRead();
            ApplyNotificationFilter("All");
            NotificationMenuPanel.Visibility = Visibility.Visible;
            AnimateNotificationMenuOpen();
        }
    }

    private void AnimateNotificationMenuOpen()
    {
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = TimeSpan.FromMilliseconds(180);

        NotificationMenuBorder.Opacity = 0;
        var opacityAnim = new DoubleAnimation(0, 1, duration) { EasingFunction = easing };
        NotificationMenuBorder.BeginAnimation(OpacityProperty, opacityAnim);

        NotificationMenuTransform.Y = -8;
        var slideAnim = new DoubleAnimation(-8, 0, duration) { EasingFunction = easing };
        NotificationMenuTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
    }

    private void CloseNotificationMenu_OnClick(object sender, RoutedEventArgs e)
    {
        NotificationMenuPanel.Visibility = Visibility.Collapsed;
    }

    private void FilterButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        var filterTag = button.Tag?.ToString() ?? "All";
        ApplyNotificationFilter(filterTag);
    }

    private void ApplyNotificationFilter(string filterTag)
    {
        _currentNotificationFilter = filterTag;

        UpdateNotificationFilterButtonVisuals();

        _filteredNotifications = filterTag switch
        {
            "Error"   => _vm.NotificationService.GetFiltered(Models.NotificationSeverity.Error),
            "Warning" => _vm.NotificationService.GetFiltered(Models.NotificationSeverity.Warning),
            "Info"    => _vm.NotificationService.GetFiltered(Models.NotificationSeverity.Info),
            _         => _vm.NotificationService.GetFiltered(null),
        };

        NotificationsItemsControl.ItemsSource = _filteredNotifications;

        var hasItems = _filteredNotifications.Any();
        NotificationsScrollViewer.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        NotificationEmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateNotificationFilterButtonVisuals()
    {
        var buttons = new[] { (FilterAllBtn, "All"), (FilterErrorsBtn, "Error"), (FilterWarningsBtn, "Warning"), (FilterInfoBtn, "Info") };
        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var transparentBrush = System.Windows.Media.Brushes.Transparent;
        var textPrimaryBrush = (System.Windows.Media.Brush)FindResource("TextPrimary");
        var whiteBrush = System.Windows.Media.Brushes.White;

        foreach (var (btn, tag) in buttons)
        {
            var isSelected = tag == _currentNotificationFilter;
            btn.Background = isSelected ? accentBrush : transparentBrush;
            btn.Foreground = isSelected ? whiteBrush : textPrimaryBrush;
            btn.BorderBrush = System.Windows.Media.Brushes.Transparent;
            btn.BorderThickness = new Thickness(0);
        }
    }

    private void DismissNotification_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button)
            return;

        var notificationId = button.Tag?.ToString();
        if (string.IsNullOrEmpty(notificationId))
            return;

        _vm.NotificationService.Dismiss(notificationId);
        ApplyNotificationFilter(_currentNotificationFilter);
        UpdateNotificationBadgeVisibility();
    }

    private void MarkAllAsRead_OnClick(object sender, RoutedEventArgs e)
    {
        _vm.NotificationService.MarkAllAsRead();
        UpdateNotificationBadgeVisibility();
    }

    private void ClearAllNotifications_OnClick(object sender, RoutedEventArgs e)
    {
        _vm.NotificationService.ClearAll();
        ApplyNotificationFilter(_currentNotificationFilter);
        UpdateNotificationBadgeVisibility();
    }

    private void UpdateNotificationBadgeVisibility()
    {
        var unreadCount = _vm.NotificationService.UnreadCount;
        NotificationBadge.Visibility = unreadCount > 0 ? Visibility.Visible : Visibility.Collapsed;
        _tray.NotificationBadgeCount = unreadCount;
    }
}
