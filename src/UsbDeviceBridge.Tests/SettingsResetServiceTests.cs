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

    [Fact]
    public async Task ResetAsync_ForgetsRememberedDevices_AndClearsLocalSettings()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.Save(new AppSettings
        {
            SetupCompleted = true,
            Theme = "Light",
            DeviceDistroSelections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["dev-1"] = "Ubuntu",
            },
        });

        var fakeClient = new FakeRememberedDeviceResetClient
        {
            RememberedIds = ["dev-1", "dev-2"],
            ForgetResults = new Dictionary<string, ForgetDeviceOutcome>(StringComparer.Ordinal)
            {
                ["dev-1"] = new ForgetDeviceOutcome(true, "Device forgotten."),
                ["dev-2"] = new ForgetDeviceOutcome(true, "Device forgotten."),
            },
        };

        var sut = new SettingsResetService(fakeClient, settingsService);

        var result = await sut.ResetAsync();
        var loaded = settingsService.Load();

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.ForgottenCount);
        Assert.Equal(["dev-1", "dev-2"], fakeClient.ForgetCalls);
        Assert.False(loaded.SetupCompleted);
        Assert.Equal("Dark", loaded.Theme);
        Assert.Empty(loaded.DeviceDistroSelections);
    }

    [Fact]
    public async Task ResetAsync_DoesNotClearLocalSettings_WhenAnyForgetFails()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.Save(new AppSettings
        {
            SetupCompleted = true,
            Theme = "Light",
        });

        var fakeClient = new FakeRememberedDeviceResetClient
        {
            RememberedIds = ["dev-1"],
            ForgetResults = new Dictionary<string, ForgetDeviceOutcome>(StringComparer.Ordinal)
            {
                ["dev-1"] = new ForgetDeviceOutcome(false, "Failed to forget."),
            },
        };

        var sut = new SettingsResetService(fakeClient, settingsService);

        var result = await sut.ResetAsync();
        var loaded = settingsService.Load();

        Assert.False(result.Succeeded);
        Assert.Contains("Failed to forget device", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(loaded.SetupCompleted);
        Assert.Equal("Light", loaded.Theme);
    }

    [Fact]
    public async Task ResetAsync_DoesNotClearLocalSettings_WhenListingRememberedDevicesFails()
    {
        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var settingsService = new AppSettingsService(settingsPath);
        settingsService.Save(new AppSettings
        {
            SetupCompleted = true,
            Theme = "Light",
        });

        var fakeClient = new FakeRememberedDeviceResetClient
        {
            ThrowOnList = true,
        };

        var sut = new SettingsResetService(fakeClient, settingsService);

        var result = await sut.ResetAsync();
        var loaded = settingsService.Load();

        Assert.False(result.Succeeded);
        Assert.Contains("Reset failed.", result.ErrorMessage, StringComparison.Ordinal);
        Assert.True(loaded.SetupCompleted);
        Assert.Equal("Light", loaded.Theme);
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

    private sealed class FakeRememberedDeviceResetClient : IRememberedDeviceResetClient
    {
        public IReadOnlyList<string> RememberedIds { get; init; } = [];

        public Dictionary<string, ForgetDeviceOutcome> ForgetResults { get; init; }
            = new(StringComparer.Ordinal);

        public List<string> ForgetCalls { get; } = [];

        public bool ThrowOnList { get; init; }

        public Task<IReadOnlyList<string>> GetRememberedInstanceIdsAsync(CancellationToken cancellationToken = default)
        {
            if (ThrowOnList)
            {
                throw new InvalidOperationException("List failed");
            }

            return Task.FromResult(RememberedIds);
        }

        public Task<ForgetDeviceOutcome> ForgetDeviceAsync(string instanceId, CancellationToken cancellationToken = default)
        {
            ForgetCalls.Add(instanceId);
            if (ForgetResults.TryGetValue(instanceId, out var result))
            {
                return Task.FromResult(result);
            }

            return Task.FromResult(new ForgetDeviceOutcome(true, "Device forgotten."));
        }
    }
}
