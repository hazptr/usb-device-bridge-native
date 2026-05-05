using System.Windows;
using Button = System.Windows.Controls.Button;
using Border = System.Windows.Controls.Border;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
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

    public TextBox AddClientHostText => SettingsAddClientHostText;

    public StackPanel ClientListHost => SettingsClientListHost;

    public Button CheckUpdatesButton => SettingsCheckUpdatesButton;

    public TextBlock CheckUpdatesStatus => SettingsCheckUpdatesStatus;

    public event RoutedEventHandler? CloseRequested;

    public event TextChangedEventHandler? SearchTextChanged;

    public event RoutedEventHandler? CheckUsbIpRequested;

    public event RoutedEventHandler? OpenUsbIpDocsRequested;

    public event RoutedEventHandler? ResetSetupRequested;

    public event RoutedEventHandler? CopyVersionInfoRequested;

    public event RoutedEventHandler? AddClientRequested;

    public event RoutedEventHandler? CheckUpdatesRequested;

    public event RoutedEventHandler? OpenReleasesPageRequested;

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
        => CloseRequested?.Invoke(sender, e);

    private void SettingsSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
        => SearchTextChanged?.Invoke(sender, e);

    private void CheckUsbIp_OnClick(object sender, RoutedEventArgs e)
        => CheckUsbIpRequested?.Invoke(sender, e);

    private void OpenUsbIpDocs_OnClick(object sender, RoutedEventArgs e)
        => OpenUsbIpDocsRequested?.Invoke(sender, e);

    private void ResetSetup_OnClick(object sender, RoutedEventArgs e)
        => ResetSetupRequested?.Invoke(sender, e);

    private void CopyVersionInfo_OnClick(object sender, RoutedEventArgs e)
        => CopyVersionInfoRequested?.Invoke(sender, e);

    private void AddClient_OnClick(object sender, RoutedEventArgs e)
        => AddClientRequested?.Invoke(sender, e);

    private void CheckForUpdates_OnClick(object sender, RoutedEventArgs e)
        => CheckUpdatesRequested?.Invoke(sender, e);

    private void OpenReleasesPage_OnClick(object sender, RoutedEventArgs e)
        => OpenReleasesPageRequested?.Invoke(sender, e);
}
