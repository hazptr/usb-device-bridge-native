using System.IO;
using System.Text.Json;

namespace UsbDeviceBridge.App.Settings;

public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _settingsPath;

    public AppSettingsService(string? settingsPath = null)
    {
        var resolvedPath = settingsPath ?? GetDefaultSettingsPath();
        var settingsDir = Path.GetDirectoryName(resolvedPath)
            ?? throw new InvalidOperationException("Settings path must include a directory.");
        Directory.CreateDirectory(settingsDir);
        _settingsPath = resolvedPath;
    }

    internal static string GetDefaultSettingsPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var settingsDir = Path.Combine(appData, "UsbDeviceBridge");
        return Path.Combine(settingsDir, "settings.json");
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            return Normalize(settings ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(_settingsPath, json);
    }

    public void Clear()
    {
        if (File.Exists(_settingsPath))
        {
            File.Delete(_settingsPath);
        }
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Theme = string.Equals(settings.Theme, "Light", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        settings.SortOrder = string.Equals(settings.SortOrder, "Name", StringComparison.OrdinalIgnoreCase)
            ? "Name"
            : "State then name";
        settings.ServiceStartupMode = ServiceStartupModes.Normalize(settings.ServiceStartupMode);
        settings.FirewallFixPolicy = FirewallFixPolicies.Normalize(settings.FirewallFixPolicy);
        return settings;
    }
}