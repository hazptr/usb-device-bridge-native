using System.IO;
using System.Text.Json;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Manages remembered devices in app-local storage.
/// Replaces service-side RememberedDeviceStore (BUG-0006 fix).
/// </summary>
public sealed class AppRememberedDeviceStore
{
    private readonly string _filePath;
    private Dictionary<string, AttachTarget> _cache = new(StringComparer.OrdinalIgnoreCase);

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

    public Dictionary<string, AttachTarget> Load()
    {
        _cache.Clear();
        if (!File.Exists(_filePath))
            return _cache;

        try
        {
            var json = File.ReadAllText(_filePath);
            var parsed = ParseJson(json);
            _cache = new Dictionary<string, AttachTarget>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            _cache = new(StringComparer.OrdinalIgnoreCase);
        }

        return _cache;
    }

    public void AddOrUpdate(string instanceId, string distro)
        => AddOrUpdate(instanceId, BuildWslTarget(distro));

    public void AddOrUpdate(string instanceId, AttachTarget target)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            return;

        _cache[instanceId] = NormalizeTarget(target);
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

    private static Dictionary<string, AttachTarget> ParseJson(string json)
    {
        var parsed = new Dictionary<string, AttachTarget>(StringComparer.OrdinalIgnoreCase);

        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
            return parsed;

        foreach (var property in doc.RootElement.EnumerateObject())
        {
            var instanceId = property.Name;
            if (string.IsNullOrWhiteSpace(instanceId))
                continue;

            var maybeTarget = TryParseTarget(property.Value);
            if (maybeTarget is not null)
                parsed[instanceId] = maybeTarget;
        }

        return parsed;
    }

    private static AttachTarget? TryParseTarget(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return BuildWslTarget(element.GetString() ?? string.Empty);

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        string name = string.Empty;
        AttachTargetType type = AttachTargetType.Unspecified;

        foreach (var prop in element.EnumerateObject())
        {
            if (prop.Name.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? string.Empty : string.Empty;
                continue;
            }

            if (prop.Name.Equals("type", StringComparison.OrdinalIgnoreCase))
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetInt32(out var numericType))
                {
                    if (numericType == (int)AttachTargetType.Wsl)
                        type = AttachTargetType.Wsl;
                    else if (numericType == (int)AttachTargetType.Ssh)
                        type = AttachTargetType.Ssh;

                    continue;
                }

                if (prop.Value.ValueKind == JsonValueKind.String)
                    type = ParseTargetType(prop.Value.GetString());
            }
        }

        return NormalizeTarget(new AttachTarget { Type = type, Name = name });
    }

    private static AttachTargetType ParseTargetType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return AttachTargetType.Unspecified;

        var normalized = value.Trim();
        if (normalized.Equals("wsl", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("attach_target_type_wsl", StringComparison.OrdinalIgnoreCase))
        {
            return AttachTargetType.Wsl;
        }

        if (normalized.Equals("ssh", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("attach_target_type_ssh", StringComparison.OrdinalIgnoreCase))
        {
            return AttachTargetType.Ssh;
        }

        return AttachTargetType.Unspecified;
    }

    private static AttachTarget BuildWslTarget(string distro)
        => new()
        {
            Type = AttachTargetType.Wsl,
            Name = (distro ?? string.Empty).Trim(),
        };

    private static AttachTarget NormalizeTarget(AttachTarget target)
    {
        var type = target.Type;
        if (type is not AttachTargetType.Wsl and not AttachTargetType.Ssh)
            type = AttachTargetType.Wsl;

        return new AttachTarget
        {
            Type = type,
            Name = (target.Name ?? string.Empty).Trim(),
        };
    }

    private void Save()
    {
        try
        {
            var toPersist = new Dictionary<string, PersistedAttachTarget>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instanceId, target) in _cache)
            {
                toPersist[instanceId] = new PersistedAttachTarget
                {
                    Type = target.Type == AttachTargetType.Ssh ? "SSH" : "WSL",
                    Name = (target.Name ?? string.Empty).Trim(),
                };
            }

            var json = JsonSerializer.Serialize(toPersist, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Best effort; file I/O failure is non-fatal.
        }
    }

    private sealed class PersistedAttachTarget
    {
        public string Type { get; set; } = "WSL";

        public string Name { get; set; } = string.Empty;
    }
}

