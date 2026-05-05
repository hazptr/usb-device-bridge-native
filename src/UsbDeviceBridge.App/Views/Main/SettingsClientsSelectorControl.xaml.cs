using System.Windows;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;

namespace UsbDeviceBridge.App.Views.Main;

public partial class SettingsClientsSelectorControl : UserControl
{
    public SettingsClientsSelectorControl()
    {
        InitializeComponent();
    }

    public TextBox AddClientHostText => AddClientHostTextBox;

    public StackPanel ClientListHost => ClientListHostPanel;

    public event RoutedEventHandler? AddClientRequested;

    private void AddClientButton_OnClick(object sender, RoutedEventArgs e)
        => AddClientRequested?.Invoke(sender, e);
}