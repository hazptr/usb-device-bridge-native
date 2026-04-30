using Forms = System.Windows.Forms;
using System.Windows;

namespace UsbDeviceBridge.App.Shell;

public sealed class TrayIconManager : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _showHideItem;
    private readonly Forms.ToolStripMenuItem _settingsItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private int _notificationBadgeCount;

    public TrayIconManager()
    {
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "USB Device Bridge for WSL",
            Visible = false,
            Icon = LoadAppIcon(),
        };

        var menu = new Forms.ContextMenuStrip();
        _showHideItem = new Forms.ToolStripMenuItem("Show", null, (_, _) => ShowRequested?.Invoke());
        _settingsItem = new Forms.ToolStripMenuItem("Settings", null, (_, _) => SettingsRequested?.Invoke());
        _exitItem = new Forms.ToolStripMenuItem("Exit", null, (_, _) => ExitRequested?.Invoke());

        menu.Items.Add(_showHideItem);
        menu.Items.Add(_settingsItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_exitItem);
        _notifyIcon.ContextMenuStrip = menu;
        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/app_icon.ico", UriKind.Absolute);
            var resource = System.Windows.Application.GetResourceStream(uri);
            if (resource?.Stream is not null)
            {
                using var stream = resource.Stream;
                return new System.Drawing.Icon(stream);
            }
        }
        catch
        {
            // Fall through to default icon.
        }

        return System.Drawing.SystemIcons.Application;
    }

    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public int NotificationBadgeCount
    {
        get => _notificationBadgeCount;
        set
        {
            _notificationBadgeCount = Math.Max(0, value);
            UpdateTooltipText();
        }
    }

    public void UpdateShowHideMenuText(bool isWindowVisible)
    {
        _showHideItem.Text = isWindowVisible ? "Hide" : "Show";
    }

    public void ShowIcon()
    {
        _notifyIcon.Visible = true;
        UpdateTooltipText();
    }

    public void HideIcon()
    {
        _notifyIcon.Visible = false;
    }

    private void UpdateTooltipText()
    {
        var suffix = _notificationBadgeCount switch
        {
            <= 0 => string.Empty,
            1 => " (1 notification)",
            _ => $" ({_notificationBadgeCount} notifications)",
        };

        _notifyIcon.Text = $"USB Device Bridge for WSL{suffix}";
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}