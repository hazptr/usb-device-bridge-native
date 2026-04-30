using Microsoft.Win32;
using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

/// <summary>
/// Tests for <see cref="WindowsStartupRegistryService"/> using an isolated HKCU subkey
/// so the real Windows Run key is never touched during testing.
/// </summary>
public sealed class WindowsStartupRegistryServiceTests : IDisposable
{
    // Use a unique subkey per test run; cleaned up in Dispose.
    private readonly string _testKeyPath;
    private const string ValueName = "UsbDeviceBridgeTest";

    public WindowsStartupRegistryServiceTests()
    {
        _testKeyPath = $@"SOFTWARE\UsbDeviceBridgeTests\{Guid.NewGuid():N}";
        // Ensure the subkey exists so tests have a clean slate.
        Registry.CurrentUser.CreateSubKey(_testKeyPath)?.Dispose();
    }

    private WindowsStartupRegistryService CreateService() =>
        new(_testKeyPath, ValueName);

    [Fact]
    public void IsEnabled_ReturnsFalse_WhenEntryDoesNotExist()
    {
        var svc = CreateService();

        Assert.False(svc.IsEnabled());
    }

    [Fact]
    public void TryEnable_CreatesEntry_AndIsEnabledReturnsTrue()
    {
        var svc = CreateService();

        var result = svc.TryEnable(@"C:\Fake\App.exe", out var error);

        Assert.True(result);
        Assert.Empty(error);
        Assert.True(svc.IsEnabled());
    }

    [Fact]
    public void TryEnable_StoresCorrectPath()
    {
        var svc = CreateService();
        const string exePath = @"C:\Program Files\UsbDeviceBridge\UsbDeviceBridge.App.exe";

        svc.TryEnable(exePath, out _);

        using var key = Registry.CurrentUser.OpenSubKey(_testKeyPath, writable: false);
        var stored = key?.GetValue(ValueName) as string;
        Assert.Equal(exePath, stored);
    }

    [Fact]
    public void TryDisable_RemovesEntry_AndIsEnabledReturnsFalse()
    {
        var svc = CreateService();
        svc.TryEnable(@"C:\Fake\App.exe", out _);
        Assert.True(svc.IsEnabled()); // pre-condition

        var result = svc.TryDisable(out var error);

        Assert.True(result);
        Assert.Empty(error);
        Assert.False(svc.IsEnabled());
    }

    [Fact]
    public void TryDisable_Succeeds_WhenEntryDoesNotExist()
    {
        var svc = CreateService();

        // Should not throw or return an error when no entry is present.
        var result = svc.TryDisable(out var error);

        Assert.True(result);
        Assert.Empty(error);
    }

    [Fact]
    public void TryEnable_ThenDisable_ThenEnable_WorksCorrectly()
    {
        var svc = CreateService();

        svc.TryEnable(@"C:\Fake\App.exe", out _);
        Assert.True(svc.IsEnabled());

        svc.TryDisable(out _);
        Assert.False(svc.IsEnabled());

        svc.TryEnable(@"C:\Fake\App.exe", out _);
        Assert.True(svc.IsEnabled());
    }

    [Fact]
    public void TryEnable_Overwrites_ExistingEntry()
    {
        var svc = CreateService();
        svc.TryEnable(@"C:\Old\Path.exe", out _);

        svc.TryEnable(@"C:\New\Path.exe", out _);

        using var key = Registry.CurrentUser.OpenSubKey(_testKeyPath, writable: false);
        var stored = key?.GetValue(ValueName) as string;
        Assert.Equal(@"C:\New\Path.exe", stored);
    }

    public void Dispose()
    {
        // Clean up the test registry subkey and all values.
        try
        {
            // Delete from the parent key
            const string parentPath = @"SOFTWARE\UsbDeviceBridgeTests";
            var leafName = _testKeyPath[(parentPath.Length + 1)..];
            using var parent = Registry.CurrentUser.OpenSubKey(parentPath, writable: true);
            parent?.DeleteSubKeyTree(leafName, throwOnMissingSubKey: false);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }
}
