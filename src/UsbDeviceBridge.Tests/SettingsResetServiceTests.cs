using UsbDeviceBridge.App.Services;
using UsbDeviceBridge.App.Settings;

namespace UsbDeviceBridge.Tests;

public sealed class SettingsResetServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsResetServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UsbDeviceBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    // Shared helpers ─────────────────────────────────────────────────────────

    // LocalDeviceManager with a non-existent binary — GetDevicesAsync throws,
    // which SettingsResetService swallows, so it still removes devices from the store.
    private static LocalDeviceManager NoopDeviceManager() =>
        new(new UsbIpdClient("fake-usbipd-does-not-exist"));

    // BridgeServiceClient that points at a port no one listens on.
    // SettingsResetService only calls Admin.UnbindDeviceAsync when a device
    // is found in state != "available"; since NoopDeviceManager returns nothing,
    // this client is never actually invoked in these tests.
    private static BridgeServiceClient NoopServiceClient() =>
        new("http://127.0.0.1:1");

    // Tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResetAsync_ForgetsRememberedDevices_AndClearsLocalSettings()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var storePath = Path.Combine(_tempDir, "remembered_devices.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.Save(new AppSettings
        {
            SetupCompleted = true,
            Theme = "Light",
        });

        var store = new AppRememberedDeviceStore(storePath);
        store.AddOrUpdate("dev-1", "Ubuntu");
        store.AddOrUpdate("dev-2", "Debian");

        var sut = new SettingsResetService(store, NoopDeviceManager(), NoopServiceClient(), settingsService);

        var result = await sut.ResetAsync();
        var loaded = settingsService.Load();
        var remaining = store.Load();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ForgottenCount);
        Assert.Empty(remaining);
        Assert.False(loaded.SetupCompleted);
        Assert.Equal("Dark", loaded.Theme);
    }

    [Fact]
    public async Task ResetAsync_ClearsSettingsWhenNoDevicesRemembered()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var storePath = Path.Combine(_tempDir, "remembered_devices.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.Save(new AppSettings { SetupCompleted = true, Theme = "Light" });

        var store = new AppRememberedDeviceStore(storePath);

        var sut = new SettingsResetService(store, NoopDeviceManager(), NoopServiceClient(), settingsService);

        var result = await sut.ResetAsync();
        var loaded = settingsService.Load();

        Assert.True(result.Succeeded);
        Assert.Equal(0, result.ForgottenCount);
        Assert.False(loaded.SetupCompleted);
    }

    [Fact]
    public async Task ResetAsync_StillClearsStore_WhenDeviceQueryFails()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var storePath = Path.Combine(_tempDir, "remembered_devices.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.Save(new AppSettings { SetupCompleted = true, Theme = "Light" });

        var store = new AppRememberedDeviceStore(storePath);
        store.AddOrUpdate("dev-1", "Ubuntu");

        // NoopDeviceManager throws; SettingsResetService swallows and still clears the store.
        var sut = new SettingsResetService(store, NoopDeviceManager(), NoopServiceClient(), settingsService);

        var result = await sut.ResetAsync();
        var remaining = store.Load();

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ForgottenCount);
        Assert.Empty(remaining);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
