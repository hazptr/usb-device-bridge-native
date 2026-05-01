using System.Windows;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing settings panel filter/search logic for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private sealed record SettingsSectionInfo(
        string Key,
        string Label,
        FrameworkElement Section,
        IReadOnlyList<FrameworkElement> Rows);

    private IReadOnlyList<FrameworkElement> _searchRows = [];
    private IReadOnlyList<SettingsSectionInfo> _settingsSections = [];
    private HashSet<FrameworkElement> _hiddenSettingRows = [];
    private readonly Dictionary<string, Button> _settingsFilterButtons = [];
    private string? _activeSettingsFilterKey;

    private async void OpenSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsOverlay.SearchBox.Focus();
        await RefreshVersionInfoAsync();
    }

    private void CloseSettingsButton_OnClick(object sender, RoutedEventArgs e)
    {
        SettingsOverlay.Visibility = Visibility.Collapsed;
    }

    private void InitializeSettingsMetadata()
    {
        var discovered = new List<SettingsSectionInfo>();
        foreach (var section in SettingsOverlay.SectionsRoot.Children.OfType<FrameworkElement>())
        {
            if (!section.Name.StartsWith("SettingsSection", StringComparison.OrdinalIgnoreCase))
                continue;

            if (section is not Border border || border.Child is not System.Windows.Controls.Panel panel)
                continue;

            var key = section.Name["SettingsSection".Length..];
            var label = panel.Children
                .OfType<TextBlock>()
                .FirstOrDefault()?.Text ?? key;

            var rows = panel.Children
                .OfType<FrameworkElement>()
                .Where(e => e.Name.StartsWith("SettingRow", StringComparison.OrdinalIgnoreCase))
                .ToList();

            discovered.Add(new SettingsSectionInfo(key, label, section, rows));
        }

        _settingsSections = discovered;
        _searchRows = discovered.SelectMany(s => s.Rows).Distinct().ToList();
        _hiddenSettingRows = _searchRows.Where(r => r.Visibility == Visibility.Collapsed).ToHashSet();
    }

    private void BuildSettingsFilterButtons()
    {
        SettingsOverlay.SectionFiltersHost.Children.Clear();
        _settingsFilterButtons.Clear();

        CreateSettingsFilterButton("All", null);
        foreach (var section in _settingsSections)
            CreateSettingsFilterButton(section.Label, section.Key);

        UpdateSettingsFilterButtonVisuals();
    }

    private void CreateSettingsFilterButton(string label, string? key)
    {
        var button = new Button
        {
            Content = label,
            Tag = key,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
            FontSize = 11,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
            Style = (Style)FindResource("SettingsFilterButton"),
        };
        button.Click += SettingsSectionFilter_OnClick;
        SettingsOverlay.SectionFiltersHost.Children.Add(button);
        _settingsFilterButtons[key ?? string.Empty] = button;
    }

    private void SettingsSectionFilter_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button)
            return;

        _activeSettingsFilterKey = button.Tag as string;
        UpdateSettingsFilterButtonVisuals();
        ApplySettingsSearch();
    }

    private void UpdateSettingsFilterButtonVisuals()
    {
        var selectedKey = _activeSettingsFilterKey ?? string.Empty;

        foreach (var (key, button) in _settingsFilterButtons)
        {
            var selected = string.Equals(key, selectedKey, StringComparison.OrdinalIgnoreCase);
            button.Background = selected
                ? (System.Windows.Media.Brush)FindResource("AccentBrush")
                : System.Windows.Media.Brushes.Transparent;
            button.Foreground = selected
                ? System.Windows.Media.Brushes.White
                : (System.Windows.Media.Brush)FindResource("TextPrimary");
            button.BorderBrush = System.Windows.Media.Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
        }
    }

    private void SettingsSearchBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        // Search intentionally disables section filter mode.
        if (!string.IsNullOrWhiteSpace(SettingsOverlay.SearchBox.Text) && _activeSettingsFilterKey is not null)
        {
            _activeSettingsFilterKey = null;
            UpdateSettingsFilterButtonVisuals();
        }

        ApplySettingsSearch();
    }

    private void ApplySettingsSearch()
    {
        var query = (SettingsOverlay.SearchBox.Text ?? string.Empty).Trim();
        if (query.Length == 0)
        {
            foreach (var row in _searchRows)
                row.Visibility = _hiddenSettingRows.Contains(row) ? Visibility.Collapsed : Visibility.Visible;

            foreach (var section in _settingsSections)
            {
                var visibleByFilter = _activeSettingsFilterKey is null
                    || string.Equals(section.Key, _activeSettingsFilterKey, StringComparison.OrdinalIgnoreCase);
                section.Section.Visibility = visibleByFilter ? Visibility.Visible : Visibility.Collapsed;
            }

            return;
        }

        foreach (var row in _searchRows)
        {
            if (_hiddenSettingRows.Contains(row)) { row.Visibility = Visibility.Collapsed; continue; }
            var tag = row.Tag?.ToString() ?? string.Empty;
            row.Visibility = tag.Contains(query, StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        foreach (var section in _settingsSections)
        {
            var hasVisibleRows = section.Rows.Count == 0 || section.Rows.Any(r => r.Visibility == Visibility.Visible);
            section.Section.Visibility = hasVisibleRows ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
