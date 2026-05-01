using System.IO;
using System.Text.Json;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Manages remembered devices in app-local storage.
/// Replaces service-side RememberedDeviceStore (BUG-0006 fix).
/// </summary>
public sealed class AppRememberedDeviceStore
{
    private readonly string _filePath;
    private Dictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath => _filePath;

    public AppRememberedDeviceStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            _filePath = filePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "UsbDeviceBridge");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "remembered_devices.json");
        }

        Load();
    }

    public Dictionary<string, string> Load()
    {
        _cache.Clear();
        if (!File.Exists(_filePath))
            return _cache;

        try
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                ?? new(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _cache = new(StringComparer.OrdinalIgnoreCase);
        }

        return _cache;
    }

    public void AddOrUpdate(string instanceId, string distro)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        _cache[instanceId] = distro ?? "";
        Save();
    }

    public bool Remove(string instanceId)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return false;

        var removed = _cache.Remove(instanceId);
        if (removed)
            Save();
        return removed;
    }

    public void Clear()
    {
        _cache.Clear();
        Save();
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best effort; file I/O failure is non-fatal.
        }
    }
}
