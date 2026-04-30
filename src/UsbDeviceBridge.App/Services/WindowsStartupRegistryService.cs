using Microsoft.Win32;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Manages the Windows Run registry entry that controls whether the UI app starts on user login.
/// Operates under HKCU and requires no elevation.
/// </summary>
public sealed class WindowsStartupRegistryService
{
    private const string DefaultRunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string DefaultValueName = "UsbDeviceBridge";

    private readonly string _runKeyPath;
    private readonly string _valueName;

    public WindowsStartupRegistryService()
        : this(DefaultRunKeyPath, DefaultValueName)
    {
    }

    /// <summary>
    /// Constructor with overridable key path and value name, for use in tests.
    /// </summary>
    public WindowsStartupRegistryService(string runKeyPath, string valueName)
    {
        _runKeyPath = runKeyPath;
        _valueName = valueName;
    }

    /// <summary>Returns true if the Run registry entry is currently set for this app.</summary>
    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: false);
        return key?.GetValue(_valueName) is not null;
    }

    /// <summary>
    /// Creates or updates the Run registry entry pointing to <paramref name="executablePath"/>.
    /// Returns true on success; populates <paramref name="error"/> on failure.
    /// </summary>
    public bool TryEnable(string executablePath, out string error)
    {
        error = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(_runKeyPath, writable: true);
            key.SetValue(_valueName, executablePath, RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Removes the Run registry entry if it exists.
    /// Returns true on success; populates <paramref name="error"/> on failure.
    /// </summary>
    public bool TryDisable(out string error)
    {
        error = string.Empty;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(_runKeyPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
