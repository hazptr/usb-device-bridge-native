using System.Text.Json;
using UsbDeviceBridge.App.Services;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Tests;

public sealed class AppRememberedDeviceStoreTypedTargetTests : IDisposable
{
    private readonly string _tempDir;

    public AppRememberedDeviceStoreTypedTargetTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "UsbDeviceBridgeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void Load_MigratesLegacyDistroString_ToTypedWslTarget()
    {
        var path = Path.Combine(_tempDir, "remembered_devices.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["dev-1"] = "Ubuntu-24.04",
        }));

        var store = new AppRememberedDeviceStore(path);
        var loaded = store.Load();

        Assert.True(loaded.ContainsKey("dev-1"));
        Assert.Equal(AttachTargetType.Wsl, loaded["dev-1"].Type);
        Assert.Equal("Ubuntu-24.04", loaded["dev-1"].Name);
    }

    [Fact]
    public void AddOrUpdate_PersistsTypedTargets()
    {
        var path = Path.Combine(_tempDir, "remembered_devices.json");
        var store = new AppRememberedDeviceStore(path);

        store.AddOrUpdate("dev-wsl", new AttachTarget { Type = AttachTargetType.Wsl, Name = "Ubuntu" });
        store.AddOrUpdate("dev-ssh", new AttachTarget { Type = AttachTargetType.Ssh, Name = "build-box" });

        var reloaded = new AppRememberedDeviceStore(path).Load();

        Assert.Equal(AttachTargetType.Wsl, reloaded["dev-wsl"].Type);
        Assert.Equal("Ubuntu", reloaded["dev-wsl"].Name);
        Assert.Equal(AttachTargetType.Ssh, reloaded["dev-ssh"].Type);
        Assert.Equal("build-box", reloaded["dev-ssh"].Name);
    }

    [Fact]
    public void AddOrUpdate_LegacyOverload_PersistsAsWslTarget()
    {
        var path = Path.Combine(_tempDir, "remembered_devices.json");
        var store = new AppRememberedDeviceStore(path);

        store.AddOrUpdate("dev-1", "Debian");
        var loaded = store.Load();

        Assert.Equal(AttachTargetType.Wsl, loaded["dev-1"].Type);
        Assert.Equal("Debian", loaded["dev-1"].Name);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}

