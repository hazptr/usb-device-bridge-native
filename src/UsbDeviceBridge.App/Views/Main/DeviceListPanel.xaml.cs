using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;

namespace UsbDeviceBridge.App.Views.Main;

public partial class DeviceListPanel : UserControl
{
    public DeviceListPanel()
    {
        InitializeComponent();
    }

    public ItemsControl DeviceItemsHost => DeviceItemsControl;
}
