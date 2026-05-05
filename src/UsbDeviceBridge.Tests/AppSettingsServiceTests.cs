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
        Assert.Equal(SshPortForwardModes.Enabled, settings.SshPortForwardMode);
        Assert.Equal(UpdateCheckModes.Automatic, settings.UpdateCheckMode);
    }

    [Theory]
    [InlineData("automatic", "automatic")]
    [InlineData("AUTOMATIC", "automatic")]
    [InlineData("notify",    "notify")]
    [InlineData("disabled",  "disabled")]
    [InlineData("unknown",   "automatic")]
    [InlineData("",          "automatic")]
    [InlineData(null,        "automatic")]
    public void UpdateCheckModes_Normalize_ReturnsExpected(string? input, string expected)
    {
        Assert.Equal(expected, UpdateCheckModes.Normalize(input));
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
            UpdateCheckMode = UpdateCheckModes.Disabled,
            StartWithWindows = true,
            SortOrder = "Name",
            ServiceStartupMode = ServiceStartupModes.OnDemand,
            WindowsNotificationsEnabled = false,
            DetachOnExit = false,
            SshPortForwardMode = SshPortForwardModes.Disabled,
        };

        service.Save(expected);
        var loaded = service.Load();

        Assert.True(loaded.SetupCompleted);
        Assert.Equal("Light", loaded.Theme);
        Assert.False(loaded.MinimizeToTray);
        Assert.True(loaded.StartMinimized);
        Assert.False(loaded.AutoRefreshEnabled);
        Assert.Equal(UpdateCheckModes.Disabled, loaded.UpdateCheckMode);
        Assert.True(loaded.StartWithWindows);
        Assert.Equal("Name", loaded.SortOrder);
        Assert.Equal(ServiceStartupModes.OnDemand, loaded.ServiceStartupMode);
        Assert.False(loaded.WindowsNotificationsEnabled);
        Assert.False(loaded.DetachOnExit);
        Assert.Equal(SshPortForwardModes.Disabled, loaded.SshPortForwardMode);
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
        });

        File.WriteAllText(path, json);

        var service = new AppSettingsService(path);
        var loaded = service.Load();

        Assert.Equal("Dark", loaded.Theme);
        Assert.Equal("State then name", loaded.SortOrder);
        Assert.Equal(ServiceStartupModes.Automatic, loaded.ServiceStartupMode);
        Assert.Equal(SshPortForwardModes.Enabled, loaded.SshPortForwardMode);
    }

    [Fact]
    public void Load_DefaultsFirewallFixPolicy_ToAsk()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        var loaded = service.Load();

        Assert.Equal(FirewallFixPolicies.Ask, loaded.FirewallFixPolicy);
    }

    [Theory]
    [InlineData("always", "always")]
    [InlineData("ALWAYS", "always")]
    [InlineData("never",  "never")]
    [InlineData("NEVER",  "never")]
    [InlineData("ask",    "ask")]
    [InlineData("unknown","ask")]
    [InlineData("",       "ask")]
    [InlineData(null,     "ask")]
    public void FirewallFixPolicies_Normalize_ReturnsExpected(string? input, string expected)
    {
        Assert.Equal(expected, FirewallFixPolicies.Normalize(input));
    }

    [Fact]
    public void Save_AndLoad_RoundTripsFirewallFixPolicy()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        service.Save(new AppSettings { FirewallFixPolicy = FirewallFixPolicies.Always });
        var loaded = service.Load();

        Assert.Equal(FirewallFixPolicies.Always, loaded.FirewallFixPolicy);
    }

    [Fact]
    public void Load_NormalizesInvalidFirewallFixPolicy_ToAsk()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var json = JsonSerializer.Serialize(new { FirewallFixPolicy = "invalid-value" });
        File.WriteAllText(path, json);

        var service = new AppSettingsService(path);
        var loaded = service.Load();

        Assert.Equal(FirewallFixPolicies.Ask, loaded.FirewallFixPolicy);
    }

    [Fact]
    public void Load_DefaultsWindowsNotificationsEnabled_ToTrue()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        var loaded = service.Load();

        Assert.True(loaded.WindowsNotificationsEnabled);
    }

    [Fact]
    public void Save_AndLoad_RoundTripsWindowsNotificationsEnabled()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        service.Save(new AppSettings { WindowsNotificationsEnabled = false });
        var loaded = service.Load();

        Assert.False(loaded.WindowsNotificationsEnabled);
    }

    [Fact]
    public void Load_DefaultsDetachOnExit_ToTrue()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        var loaded = service.Load();

        Assert.True(loaded.DetachOnExit);
    }

    [Fact]
    public void Save_AndLoad_RoundTripsDetachOnExit()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var service = new AppSettingsService(path);

        service.Save(new AppSettings { DetachOnExit = false });
        var loaded = service.Load();

        Assert.False(loaded.DetachOnExit);
    }

    [Fact]
    public void Load_NormalizesInvalidSshPortForwardMode_ToEnabled()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        var json = JsonSerializer.Serialize(new { SshPortForwardMode = "invalid-value" });
        File.WriteAllText(path, json);

        var service = new AppSettingsService(path);
        var loaded = service.Load();

        Assert.Equal(SshPortForwardModes.Enabled, loaded.SshPortForwardMode);
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
