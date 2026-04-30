using System.Windows;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBox = System.Windows.Controls.TextBox;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using TextChangedEventHandler = System.Windows.Controls.TextChangedEventHandler;
using UserControl = System.Windows.Controls.UserControl;

namespace UsbDeviceBridge.App.Views.Main;

public partial class SettingsOverlayControl : UserControl
{
    public SettingsOverlayControl()
    {
        InitializeComponent();
    }

    public Border PanelShell => SettingsPanelShell;

    public TextBox SearchBox => SettingsSearchBox;

    public StackPanel SectionFiltersHost => SettingsSectionFiltersHost;

    public StackPanel SectionsRoot => SettingsSectionsRoot;

    public event RoutedEventHandler? CloseRequested;

    public event TextChangedEventHandler? SearchTextChanged;

    public event RoutedEventHandler? InstallUsbIpRequested;

    public event RoutedEventHandler? CheckUsbIpRequested;

    public event RoutedEventHandler? OpenUsbIpDocsRequested;

    public event RoutedEventHandler? ResetSetupRequested;

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(sender, e);

    private void SettingsSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
        => SearchTextChanged?.Invoke(sender, e);

    private void InstallUsbIpStub_OnClick(object sender, RoutedEventArgs e)
        => InstallUsbIpRequested?.Invoke(sender, e);

    private void CheckUsbIpStub_OnClick(object sender, RoutedEventArgs e)
        => CheckUsbIpRequested?.Invoke(sender, e);

    private void OpenUsbIpDocs_OnClick(object sender, RoutedEventArgs e)
        => OpenUsbIpDocsRequested?.Invoke(sender, e);

    private void ResetSetup_OnClick(object sender, RoutedEventArgs e)
        => ResetSetupRequested?.Invoke(sender, e);
}
