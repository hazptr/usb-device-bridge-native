using System.Text.Json;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.Tests;

public sealed class AppSettingsServiceTests : IDisposable
{
    private readonly string _tempDir;

    public AppSettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UsbDeviceBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Load_ReturnsDefaults_WhenFileDoesNotExist()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        var settings = service.Load();

        Assert.Equal("Dark", settings.Theme);
        Assert.True(settings.MinimizeToTray);
        Assert.True(settings.AutoRefreshEnabled);
        Assert.Equal("State then name", settings.SortOrder);
        Assert.Equal(ServiceStartupModes.Automatic, settings.ServiceStartupMode);
        Assert.Empty(settings.DeviceDistroSelections);
    }

    [Fact]
    public void Save_AndLoad_RoundTripsPersistedSettings()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        var expected = new AppSettings
        {
            SetupCompleted = true,
            Theme = "Light",
            MinimizeToTray = false,
            StartMinimized = true,
            AutoRefreshEnabled = false,
            AutoUpdateEnabled = false,
            StartWithWindows = true,
            SortOrder = "Name",
            ServiceStartupMode = ServiceStartupModes.OnDemand,
            DeviceDistroSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["USB\\VID_0A12&PID_0001"] = "Ubuntu",
                ["USB\\VID_2C97&PID_0001"] = "Debian",
            },
        };

        service.Save(expected);
        var loaded = service.Load();

        Assert.True(loaded.SetupCompleted);
        Assert.Equal("Light", loaded.Theme);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.StartMinimized);
        Assert.False(loaded.AutoRefreshEnabled);
        Assert.False(loaded.AutoUpdateEnabled);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal("Name", loaded.SortOrder);
        Assert.Equal(ServiceStartupModes.OnDemand, loaded.ServiceStartupMode);
        Assert.Equal("Ubuntu", loaded.DeviceDistroSelections["USB\\VID_0A12&PID_0001"]);
        Assert.Equal("Debian", loaded.DeviceDistroSelections["USB\\VID_2C97&PID_0001"]);
    }

    [Fact]
    public void Load_NormalizesInvalidValues_AndMalformedCollections()
    {
        var path = Path.Combine(_tempDir, "settings.json");

        var json = JsonSerializer.Serialize(new
        {
            Theme = "invalid",
            SortOrder = "unknown",
            ServiceStartupMode = "custom",
            DeviceDistroSelections = (Dictionary<string, string>?)null,
        });

        File.WriteAllText(path, json);

        var service = new AppSettingsService(path);
        var loaded = service.Load();

        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("State then name", loaded.SortOrder);
        Assert.Equal(ServiceStartupModes.Automatic, loaded.ServiceStartupMode);
        Assert.NotNull(loaded.DeviceDistroSelections);
        Assert.Empty(loaded.DeviceDistroSelections);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup for temp test folders.
        }
    }
}
