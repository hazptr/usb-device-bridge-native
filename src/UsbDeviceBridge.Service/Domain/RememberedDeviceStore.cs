using System.Text.Json;

namespace UsbDeviceBridge.Service.Domain;

public sealed class RememberedDeviceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _filePath;
    private readonly object _lock = new();

    public RememberedDeviceStore(string? filePath = null)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var fullPath = Path.GetFullPath(filePath);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            _filePath = fullPath;
            return;
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "UsbDeviceBridge"
        );
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "remembered_devices.json");
    }

    public string FilePath => _filePath;

    public IReadOnlyDictionary<string, string> Load()
    {
        lock (_lock) { return LoadUnlocked(); }
    }

    public void AddOrUpdate(string instanceId, string preferredDistro)
    {
        lock (_lock)
        {
            var entries = LoadUnlocked();
            entries[instanceId] = preferredDistro;
            SaveUnlocked(entries);
        }
    }

    public bool Remove(string instanceId)
    {
        lock (_lock)
        {
            var entries = LoadUnlocked();
            if (!entries.Remove(instanceId)) return false;
            SaveUnlocked(entries);
            return true;
        }
    }

    private Dictionary<string, string> LoadUnlocked()
    {
        if (!File.Exists(_filePath)) return new();
        try
        {
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch { return new(); }
    }

    private void SaveUnlocked(Dictionary<string, string> entries)
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(entries, JsonOptions));
    }
}
