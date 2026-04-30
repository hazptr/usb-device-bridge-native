namespace UsbDeviceBridge.App.Settings;

public static class ServiceStartupModes
{
    public const string Automatic = "Automatic";
    public const string OnDemand = "On-Demand";
    public const string Manual = "Manual";

    public static readonly IReadOnlyList<string> All = [Automatic, OnDemand, Manual];

    public static string Normalize(string? value)
    {
        if (string.Equals(value, OnDemand, StringComparison.OrdinalIgnoreCase))
        {
            return OnDemand;
        }

        if (string.Equals(value, Manual, StringComparison.OrdinalIgnoreCase))
        {
            return Manual;
        }

        return Automatic;
    }
}

public sealed class AppSettings
{
    public bool SetupCompleted { get; set; }

    public string Theme { get; set; } = "Dark";

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool AutoRefreshEnabled { get; set; } = true;

    public bool AutoUpdateEnabled { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public string SortOrder { get; set; } = "State then name";

    public string ServiceStartupMode { get; set; } = ServiceStartupModes.Automatic;

    public Dictionary<string, string> DeviceDistroSelections { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}