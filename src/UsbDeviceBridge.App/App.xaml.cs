using System.Windows;
using UsbDeviceBridge.App.Services;
using UsbDeviceBridge.App.Settings;
using UsbDeviceBridge.App.Shell;
using UsbDeviceBridge.App.Theming;

namespace UsbDeviceBridge.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceCoordinator? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length > 0)
        {
            // CLI mode — run command and exit without showing any window.
            await CliRunner.RunAsync(e.Args);
            Shutdown(0);
            return;
        }

        _singleInstance = new SingleInstanceCoordinator();
        if (!_singleInstance.IsPrimaryInstance)
        {
            await SingleInstanceCoordinator.NotifyPrimaryInstanceAsync();
            Shutdown(0);
            return;
        }

        _singleInstance.ActivationRequested += OnActivationRequested;
        _singleInstance.StartListening();

        var settingsService = new AppSettingsService();
        var settings = settingsService.Load();

        // Check for force setup via environment variable (useful for testing/development)
        var forceSetup = Environment.GetEnvironmentVariable("USB_DEVICE_BRIDGE_FORCE_SETUP") == "1";
        var isFirstRun = !settings.SetupCompleted || forceSetup;

        // Discover available themes from the manifest generated at build time.
        ThemeManager.Initialize();

        // Apply dark theme by default for first run; saved theme otherwise.
        ThemeManager.ApplyTheme(isFirstRun ? "Dark" : settings.Theme);

        var window = new MainWindow(settingsService, settings, isFirstRun, forceSetup);
        MainWindow = window;
        window.Show();

        ShutdownMode = ShutdownMode.OnMainWindowClose;

        if (!isFirstRun && settings.StartMinimized)
        {
            window.StartMinimizedToTrayIfEnabled();
        }
    }

    private void OnActivationRequested()
    {
        Dispatcher.Invoke(() =>
        {
            if (MainWindow is MainWindow window)
            {
                window.RestoreAndActivate();
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_singleInstance is not null)
        {
            _singleInstance.ActivationRequested -= OnActivationRequested;
            _singleInstance.Dispose();
            _singleInstance = null;
        }

        base.OnExit(e);
    }
}
