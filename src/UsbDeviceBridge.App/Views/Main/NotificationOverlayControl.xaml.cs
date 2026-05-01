using System.Windows;
using System.Windows.Media;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using ItemsControl = System.Windows.Controls.ItemsControl;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using UserControl = System.Windows.Controls.UserControl;

namespace UsbDeviceBridge.App.Views.Main;

public partial class NotificationOverlayControl : UserControl
{
    public NotificationOverlayControl()
    {
        InitializeComponent();
    }

    public Border MenuBorder => NotificationMenuBorder;

    public TranslateTransform MenuTransform => NotificationMenuTransform;

    public ItemsControl NotificationsItems => NotificationsItemsControl;

    public ScrollViewer NotificationsScroll => NotificationsScrollViewer;

    public Border EmptyState => NotificationEmptyState;

    public Button FilterAllButton => FilterAllBtn;

    public Button FilterErrorsButton => FilterErrorsBtn;

    public Button FilterWarningsButton => FilterWarningsBtn;

    public Button FilterInfoButton => FilterInfoBtn;

    public event RoutedEventHandler? CloseRequested;

    public event RoutedEventHandler? FilterRequested;

    public event RoutedEventHandler? DismissRequested;

    public event RoutedEventHandler? CopyRequested;

    public event RoutedEventHandler? MarkAllAsReadRequested;

    public event RoutedEventHandler? ClearAllRequested;

    private void CloseNotificationMenu_OnClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(sender, e);

    private void FilterButton_OnClick(object sender, RoutedEventArgs e)
        => FilterRequested?.Invoke(sender, e);

    private void DismissNotification_OnClick(object sender, RoutedEventArgs e)
        => DismissRequested?.Invoke(sender, e);

    private void CopyNotification_OnClick(object sender, RoutedEventArgs e)
        => CopyRequested?.Invoke(sender, e);

    private void MarkAllAsRead_OnClick(object sender, RoutedEventArgs e)
        => MarkAllAsReadRequested?.Invoke(sender, e);

    private void ClearAllNotifications_OnClick(object sender, RoutedEventArgs e)
        => ClearAllRequested?.Invoke(sender, e);
}
