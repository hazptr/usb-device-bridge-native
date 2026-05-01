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

/// <summary>
/// Policy for automatically applying the WSL vEthernet Public-profile firewall fix
/// when usbipd attach output suggests a firewall block.
/// </summary>
public static class FirewallFixPolicies
{
    public const string Ask    = "ask";
    public const string Always = "always";
    public const string Never  = "never";

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Always, StringComparison.OrdinalIgnoreCase)) return Always;
        if (string.Equals(value, Never,  StringComparison.OrdinalIgnoreCase)) return Never;
        return Ask;
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

    /// <summary>
    /// Controls automatic WSL vEthernet Public-profile firewall fix when attach is blocked.
    /// Accepted values: "ask" (default), "always", "never".
    /// Invalid or missing values are normalised to "ask".
    /// </summary>
    public string FirewallFixPolicy { get; set; } = FirewallFixPolicies.Ask;

    /// <summary>
    /// Controls whether the app sends OS-level Action Center notifications
    /// when the window is not focused. In-app toasts are always shown.
    /// </summary>
    public bool WindowsNotificationsEnabled { get; set; } = true;

    /// <summary>
    /// Controls whether all attached/shared USB devices are detached and unbound
    /// when the app closes. Remembered devices will be re-attached on next launch.
    /// </summary>
    public bool DetachOnExit { get; set; } = true;

}