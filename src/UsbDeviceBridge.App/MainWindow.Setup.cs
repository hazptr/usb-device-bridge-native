using System.Diagnostics;
using System.Windows;
using System.Windows.Media.Animation;
using Grpc.Core;
using Usbdevicebridge.V1;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using Ellipse = System.Windows.Shapes.Ellipse;
using Grid = System.Windows.Controls.Grid;
using Rectangle = System.Windows.Shapes.Rectangle;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace UsbDeviceBridge.App;

/// <summary>
/// Partial class containing the four-step setup wizard logic for <see cref="MainWindow"/>.
/// </summary>
public partial class MainWindow
{
    private int _setupStepIndex;
    private string _setupSelectedTheme = "Dark";
    private bool _setupForceShowingOverlay;
    private List<(string Name, string Status, string Message)>? _setupPrerequisitesStatus;
    private Dictionary<string, bool> _setupSelectedDistros = new();
    private CancellationTokenSource? _setupInstallCts;
    private bool _setupPrerequisitesVerifiedInstalled;
    private bool _deviceInitializationStarted;

    private Grid SetupStepOnePanel => SetupOverlay.StepOnePanel;

    private Grid SetupStepTwoPanelPrereq => SetupOverlay.StepTwoPanelPrereq;

    private Grid SetupStepThreePanelDistro => SetupOverlay.StepThreePanelDistro;

    private Grid SetupStepFourPanel => SetupOverlay.StepFourPanel;

    private Grid SetupDistroSelectionView => SetupOverlay.DistroSelectionView;

    private Grid SetupDistroLogView => SetupOverlay.DistroLogView;

    private StackPanel SetupDistroCheckboxes => SetupOverlay.DistroCheckboxes;

    private StackPanel SetupPrerequisitesStatus => SetupOverlay.PrerequisitesStatus;

    private TextBox SetupInstallLogText => SetupOverlay.InstallLogText;

    private Button SetupInstallPackagesButton => SetupOverlay.InstallPackagesButton;

    private Button SetupInstallStopButton => SetupOverlay.InstallStopButton;

    private Button SetupInstallStartOverButton => SetupOverlay.InstallStartOverButton;

    private Button SetupBackButton => SetupOverlay.BackButton;

    private Button SetupNextButton => SetupOverlay.NextButton;

    private Button SetupDarkCard => SetupOverlay.DarkCard;

    private Button SetupLightCard => SetupOverlay.LightCard;

    private TextBlock SetupDarkLabel => SetupOverlay.DarkLabel;

    private TextBlock SetupLightLabel => SetupOverlay.LightLabel;

    private Rectangle SetupDarkSwatch1 => SetupOverlay.DarkSwatch1;

    private Rectangle SetupDarkSwatch2 => SetupOverlay.DarkSwatch2;

    private Rectangle SetupDarkSwatch3 => SetupOverlay.DarkSwatch3;

    private Rectangle SetupLightSwatch1 => SetupOverlay.LightSwatch1;

    private Rectangle SetupLightSwatch2 => SetupOverlay.LightSwatch2;

    private Rectangle SetupLightSwatch3 => SetupOverlay.LightSwatch3;

    private Ellipse SetupDotOne => SetupOverlay.DotOne;

    private Ellipse SetupDotTwo => SetupOverlay.DotTwo;

    private Ellipse SetupDotThree => SetupOverlay.DotThree;

    private Ellipse SetupDotFour => SetupOverlay.DotFour;

    private CheckBox SetupEnableTray => SetupOverlay.EnableTray;

    private CheckBox SetupStartMinimized => SetupOverlay.StartMinimized;

    private CheckBox SetupAutoRefresh => SetupOverlay.AutoRefresh;

    private CheckBox SetupAutoUpdate => SetupOverlay.AutoUpdate;

    private void InitializeSetupOverlayHandlers()
    {
        // Guard against duplicate subscriptions if initialization is called again.
        SetupDarkCard.Click -= SetupThemeCard_OnClick;
        SetupLightCard.Click -= SetupThemeCard_OnClick;
        SetupBackButton.Click -= SetupBack_OnClick;
        SetupNextButton.Click -= SetupNext_OnClick;
        SetupInstallPackagesButton.Click -= SetupInstallPackages_OnClick;
        SetupInstallStopButton.Click -= SetupInstallStop_OnClick;
        SetupInstallStartOverButton.Click -= SetupInstallStartOver_OnClick;

        SetupDarkCard.Click += SetupThemeCard_OnClick;
        SetupLightCard.Click += SetupThemeCard_OnClick;
        SetupBackButton.Click += SetupBack_OnClick;
        SetupNextButton.Click += SetupNext_OnClick;
        SetupInstallPackagesButton.Click += SetupInstallPackages_OnClick;
        SetupInstallStopButton.Click += SetupInstallStop_OnClick;
        SetupInstallStartOverButton.Click += SetupInstallStartOver_OnClick;
    }

    private void ShowSetupOverlay()
    {
        _setupStepIndex = 0;
        _setupSelectedTheme = "Dark";
        _setupPrerequisitesVerifiedInstalled = false;
        UpdateSetupStepUi();
        ApplySetupCardPreviews();
        ApplySetupThemeCardSelection();
        SetupOverlay.Visibility = Visibility.Visible;
    }

    private void SetupThemeCard_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button card)
            return;

        _setupSelectedTheme = card.Tag?.ToString() == "Light" ? "Light" : "Dark";
        Theming.ThemeManager.ApplyTheme(_setupSelectedTheme);
        ApplySetupThemeCardSelection();
    }

    private void SetupBack_OnClick(object sender, RoutedEventArgs e)
    {
        if (_setupStepIndex > 0)
        {
            if (_setupStepIndex == 2 && SetupDistroLogView.Visibility == Visibility.Visible)
            {
                _setupInstallCts?.Cancel();
                SetupInstallLogText.Text = string.Empty;
                SetupDistroLogView.Visibility = Visibility.Collapsed;
                SetupDistroSelectionView.Visibility = Visibility.Visible;
                SetupNextButton.IsEnabled = true;
                return;
            }
            _setupStepIndex--;
            UpdateSetupStepUi();
        }
    }

    private async void SetupNext_OnClick(object sender, RoutedEventArgs e)
    {
        if (_setupStepIndex == 0)
        {
            _setupStepIndex = 1;
            UpdateSetupStepUi();
            _ = PopulatePrerequisitesStatusAsync();
            return;
        }

        if (_setupStepIndex == 1)
        {
            _setupStepIndex = 2;
            UpdateSetupStepUi();
            _ = PopulateDistroCheckboxesAsync();
            return;
        }

        if (_setupStepIndex == 2)
        {
            SetupInstallLogText.Text = string.Empty;
            SetupInstallStartOverButton.Visibility = Visibility.Collapsed;
            SetupDistroLogView.Visibility = Visibility.Collapsed;
            SetupDistroSelectionView.Visibility = Visibility.Visible;
            _setupStepIndex = 3;
            UpdateSetupStepUi();
            return;
        }

        // Finish — persist choices, kick off device load, then fade the overlay away.
        _settings.SetupCompleted = true;
        _settings.Theme = Theming.ThemeManager.NormalizeTheme(_setupSelectedTheme);
        _settings.MinimizeToTray = SetupEnableTray.IsChecked == true;
        _settings.StartMinimized = SetupStartMinimized.IsChecked == true;
        _settings.AutoRefreshEnabled = SetupAutoRefresh.IsChecked == true;
        _settings.AutoUpdateEnabled = SetupAutoUpdate.IsChecked == true;
        _vm.IsAutoRefresh = _settings.AutoRefreshEnabled;
        _settingsService.Save(_settings);

        await DismissSetupOverlayAsync();
        await TryInitializeDevicesAfterPrerequisitesAsync(verifyWithService: false);
    }

    private async Task<bool> QueryPrerequisitesInstalledAsync()
    {
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var response = await _client.Setup.CheckPrerequisitesAsync(
                new CheckPrerequisitesRequest(),
                deadline: deadline);

            _setupPrerequisitesStatus = response.Prerequisites
                .Select(p => (p.Name, p.Status, p.Message))
                .ToList();

            _setupPrerequisitesVerifiedInstalled = response.Prerequisites.All(
                p => string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase));

            return _setupPrerequisitesVerifiedInstalled;
        }
        catch (RpcException)
        {
            _setupPrerequisitesVerifiedInstalled = false;
            return false;
        }
    }

    private async Task TryInitializeDevicesAfterPrerequisitesAsync(bool verifyWithService)
    {
        if (_deviceInitializationStarted)
            return;

        if (SetupOverlay.Visibility == Visibility.Visible)
            return;

        var prerequisitesInstalled = _setupPrerequisitesVerifiedInstalled;
        if (!prerequisitesInstalled && verifyWithService)
            prerequisitesInstalled = await QueryPrerequisitesInstalledAsync();

        if (!prerequisitesInstalled)
        {
            _vm.StatusText = "Setup required: install prerequisites";
            if (SetupOverlay.Visibility != Visibility.Visible)
                ShowSetupOverlay();
            return;
        }

        _deviceInitializationStarted = true;
        await _vm.InitializeAsync();
    }

    private async Task PopulateDistroCheckboxesAsync()
    {
        SetupDistroCheckboxes.Children.Clear();
        _setupSelectedDistros.Clear();

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Querying WSL distros...",
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        SetupDistroCheckboxes.Children.Add(loadingText);

        IReadOnlyList<DistroInfo> distros;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            var response = await _client.Setup.QueryDistrosAsync(
                new QueryDistrosRequest(),
                deadline: deadline);
            distros = response.Distros;
        }
        catch (RpcException ex)
        {
            var isDown = ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;
            SetupDistroCheckboxes.Children.Clear();
            SetupDistroCheckboxes.Children.Add(
                CreateServiceErrorPanel(
                    isDown
                        ? "The background service isn't running."
                        : $"Service error: {ex.Status.Detail}",
                    isDown,
                    () => PopulateDistroCheckboxesAsync()));
            return;
        }

        SetupDistroCheckboxes.Children.Clear();

        if (distros.Count == 0)
        {
            SetupDistroCheckboxes.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No WSL distros found. You can install packages later from Settings.",
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (var distro in distros)
        {
            _setupSelectedDistros[distro.Name] = false;

            var checkboxPanel = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 12)
            };

            var checkbox = new System.Windows.Controls.CheckBox
            {
                Style = (System.Windows.Style)FindResource("ModernCheckBox"),
                IsChecked = false,
                Margin = new Thickness(0, 0, 12, 0),
                Tag = distro.Name
            };

            checkbox.Checked += (s, e) =>
            {
                if (checkbox.Tag is string name) _setupSelectedDistros[name] = true;
            };
            checkbox.Unchecked += (s, e) =>
            {
                if (checkbox.Tag is string name) _setupSelectedDistros[name] = false;
            };

            var label = distro.Version.Length > 0
                ? $"{distro.Name} (WSL{distro.Version})"
                : distro.Name;

            var nameText = new System.Windows.Controls.TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            };

            checkboxPanel.Children.Add(checkbox);
            checkboxPanel.Children.Add(nameText);
            SetupDistroCheckboxes.Children.Add(checkboxPanel);
        }
    }

    private void SetupInstallPackages_OnClick(object sender, RoutedEventArgs e)
    {
        var selected = _setupSelectedDistros
            .Where(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();

        if (selected.Count == 0)
        {
            _setupStepIndex = 3;
            UpdateSetupStepUi();
            return;
        }

        _ = RunDistroInstallAsync(selected);
    }

    private async Task RunDistroInstallAsync(IReadOnlyList<string> distros)
    {
        SetupDistroSelectionView.Visibility = Visibility.Collapsed;
        SetupDistroLogView.Visibility = Visibility.Visible;
        SetupNextButton.IsEnabled = false;
        SetupBackButton.IsEnabled = false;
        SetupInstallLogText.Text = string.Empty;
        SetupInstallStopButton.IsEnabled = true;
        SetupInstallStopButton.Visibility = Visibility.Visible;
        SetupInstallStartOverButton.Visibility = Visibility.Collapsed;

        _setupInstallCts = new CancellationTokenSource();
        var ct = _setupInstallCts.Token;

        bool success = false;
        bool hadErrors = false;
        try
        {
            var request = new ConfigureDistrosRequest();
            request.DistroNames.AddRange(distros);

            using var call = _client.Setup.ConfigureDistros(request);
            await foreach (var evt in call.ResponseStream.ReadAllAsync(ct))
            {
                if (evt.IsError && evt.ExitCode != 0)
                    hadErrors = true;
                AppendInstallLog(evt.OutputLine, evt.IsError);
            }
            success = !hadErrors;
        }
        catch (OperationCanceledException)
        {
            AppendInstallLog("\n— Installation stopped —", false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            AppendInstallLog("\n— Installation stopped —", false);
        }
        catch (RpcException ex)
        {
            AppendInstallLog($"\n✗ Error: {ex.Status.Detail}", true);
        }
        finally
        {
            _setupInstallCts?.Dispose();
            _setupInstallCts = null;
        }

        SetupInstallStopButton.IsEnabled = false;
        SetupInstallStopButton.Visibility = Visibility.Collapsed;
        SetupInstallStartOverButton.Visibility = Visibility.Visible;
        SetupNextButton.IsEnabled = true;
        SetupBackButton.IsEnabled = true;

        if (success)
            SetupNextButton.Content = "Next →";
    }

    private void AppendInstallLog(string line, bool isError)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendInstallLog(line, isError));
            return;
        }

        SetupInstallLogText.AppendText(line.Replace("\0", string.Empty) + "\n");
        SetupInstallLogText.ScrollToEnd();
    }

    private void SetupInstallStop_OnClick(object sender, RoutedEventArgs e)
    {
        _setupInstallCts?.Cancel();
    }

    private void SetupInstallStartOver_OnClick(object sender, RoutedEventArgs e)
    {
        SetupInstallLogText.Text = string.Empty;
        SetupInstallStartOverButton.Visibility = Visibility.Collapsed;
        SetupDistroLogView.Visibility = Visibility.Collapsed;
        SetupDistroSelectionView.Visibility = Visibility.Visible;
        SetupNextButton.IsEnabled = true;
        SetupBackButton.IsEnabled = true;
    }

    private async Task PopulatePrerequisitesStatusAsync()
    {
        SetupPrerequisitesStatus.Children.Clear();
        SetupNextButton.IsEnabled = false;

        var loadingText = new System.Windows.Controls.TextBlock
        {
            Text = "Checking prerequisites...",
            Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
            Margin = new Thickness(0, 4, 0, 0)
        };
        SetupPrerequisitesStatus.Children.Add(loadingText);

        CheckPrerequisitesResponse response;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            response = await _client.Setup.CheckPrerequisitesAsync(
                new CheckPrerequisitesRequest(),
                deadline: deadline);

            _setupPrerequisitesStatus = response.Prerequisites
                .Select(p => (p.Name, p.Status, p.Message))
                .ToList();
            _setupPrerequisitesVerifiedInstalled = response.Prerequisites.All(
                p => string.Equals(p.Status, "installed", StringComparison.OrdinalIgnoreCase));
        }
        catch (RpcException ex)
        {
            var isDown = ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded;
            _setupPrerequisitesVerifiedInstalled = false;
            SetupPrerequisitesStatus.Children.Clear();
            SetupPrerequisitesStatus.Children.Add(
                CreateServiceErrorPanel(
                    isDown
                        ? "The background service isn't running."
                        : $"Service error: {ex.Status.Detail}",
                    isDown,
                    () => PopulatePrerequisitesStatusAsync()));
            SetupNextButton.IsEnabled = true;
            return;
        }

        SetupPrerequisitesStatus.Children.Clear();

        foreach (var prereq in response.Prerequisites)
        {
            var itemStack = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 12) };

            var headerStack = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };

            var isInstalled = prereq.Status == "installed";
            var statusSymbol = new System.Windows.Controls.TextBlock
            {
                Text = isInstalled ? "✓ " : "✗ ",
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Foreground = isInstalled
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.Green)
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.OrangeRed)
            };

            var nameText = new System.Windows.Controls.TextBlock
            {
                Text = prereq.Name,
                FontWeight = FontWeights.SemiBold,
                Foreground = (System.Windows.Media.Brush)FindResource("TextPrimary")
            };

            headerStack.Children.Add(statusSymbol);
            headerStack.Children.Add(nameText);

            var messageText = new System.Windows.Controls.TextBlock
            {
                Text = prereq.Message.Length > 0 ? prereq.Message : prereq.Status,
                FontSize = 11,
                Foreground = (System.Windows.Media.Brush)FindResource("TextMuted"),
                TextWrapping = TextWrapping.Wrap
            };

            itemStack.Children.Add(headerStack);
            itemStack.Children.Add(messageText);
            SetupPrerequisitesStatus.Children.Add(itemStack);
        }

        SetupNextButton.IsEnabled = true;
    }

    private void UpdateSetupStepUi()
    {
        SetupStepOnePanel.Visibility = _setupStepIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetupStepTwoPanelPrereq.Visibility = _setupStepIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        SetupStepThreePanelDistro.Visibility = _setupStepIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SetupStepFourPanel.Visibility = _setupStepIndex == 3 ? Visibility.Visible : Visibility.Collapsed;

        SetupBackButton.IsEnabled = _setupStepIndex > 0;
        SetupNextButton.Content = _setupStepIndex == 3 ? "Finish"
                                : _setupStepIndex == 2 ? "Skip"
                                : "Next";

        var accentBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var borderBrush = (System.Windows.Media.Brush)FindResource("BorderBrush");

        SetupDotOne.Fill   = _setupStepIndex == 0 ? accentBrush : borderBrush;
        SetupDotTwo.Fill   = _setupStepIndex == 1 ? accentBrush : borderBrush;
        SetupDotThree.Fill = _setupStepIndex == 2 ? accentBrush : borderBrush;
        SetupDotFour.Fill  = _setupStepIndex == 3 ? accentBrush : borderBrush;
    }

    private void ApplySetupCardPreviews()
    {
        ApplyCardPreview("Dark",  SetupDarkCard,  SetupDarkLabel,  SetupDarkSwatch1,  SetupDarkSwatch2,  SetupDarkSwatch3);
        ApplyCardPreview("Light", SetupLightCard, SetupLightLabel, SetupLightSwatch1, SetupLightSwatch2, SetupLightSwatch3);
    }

    private static void ApplyCardPreview(
        string themeName,
        System.Windows.Controls.Button card,
        System.Windows.Controls.TextBlock label,
        System.Windows.Shapes.Rectangle swatch1,
        System.Windows.Shapes.Rectangle swatch2,
        System.Windows.Shapes.Rectangle swatch3)
    {
        var p = Theming.ThemeManager.GetPreview(themeName);
        card.Background  = new System.Windows.Media.SolidColorBrush(p.CardBackground);
        label.Foreground = new System.Windows.Media.SolidColorBrush(p.TextPrimary);
        swatch1.Fill     = new System.Windows.Media.SolidColorBrush(p.TextMuted);
        swatch2.Fill     = new System.Windows.Media.SolidColorBrush(p.Accent);
        swatch3.Fill     = new System.Windows.Media.SolidColorBrush(p.Success);
    }

    private void ApplySetupThemeCardSelection()
    {
        var selected = (System.Windows.Media.Brush)FindResource("AccentBrush");
        var unselected = (System.Windows.Media.Brush)FindResource("BorderBrush");

        SetupDarkCard.BorderBrush = _setupSelectedTheme == "Dark" ? selected : unselected;
        SetupDarkCard.BorderThickness = _setupSelectedTheme == "Dark" ? new Thickness(2) : new Thickness(1);
        SetupLightCard.BorderBrush = _setupSelectedTheme == "Light" ? selected : unselected;
        SetupLightCard.BorderThickness = _setupSelectedTheme == "Light" ? new Thickness(2) : new Thickness(1);
    }

    private Task DismissSetupOverlayAsync()
    {
        if (SetupOverlay.Visibility != Visibility.Visible)
            return Task.CompletedTask;

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            SetupOverlay.Visibility = Visibility.Collapsed;
            SetupOverlay.Opacity = 1.0;
            completion.TrySetResult();
        };
        SetupOverlay.BeginAnimation(OpacityProperty, fade);
        return completion.Task;
    }
}
