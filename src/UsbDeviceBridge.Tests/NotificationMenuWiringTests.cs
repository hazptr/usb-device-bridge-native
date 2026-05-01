using System.Text.RegularExpressions;

namespace UsbDeviceBridge.Tests;

/// <summary>
/// Regression tests for notification menu event wiring.
/// </summary>
public sealed class NotificationMenuWiringTests
{
    [Fact]
    public void MainWindow_WiresCopyRequested_ToCopyHandler()
    {
        var mainWindowPath = GetRepoFilePath("src", "UsbDeviceBridge.App", "MainWindow.xaml.cs");
        var source = File.ReadAllText(mainWindowPath);

        var pattern = @"NotificationMenuPanel\.CopyRequested\s*\+=\s*CopyNotification_OnClick\s*;";
        Assert.Matches(new Regex(pattern), source);
    }

    [Fact]
    public void NotificationOverlayControl_DeclaresCopyRequestedEvent()
    {
        var overlayControlPath = GetRepoFilePath("src", "UsbDeviceBridge.App", "Views", "Main", "NotificationOverlayControl.xaml.cs");
        var source = File.ReadAllText(overlayControlPath);

        var pattern = @"event\s+RoutedEventHandler\?\s+CopyRequested\s*;";
        Assert.Matches(new Regex(pattern), source);
    }

    private static string GetRepoFilePath(params string[] pathSegments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UsbDeviceBridgeNative.slnx")))
            {
                return Path.Combine(directory.FullName, Path.Combine(pathSegments));
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }
}
