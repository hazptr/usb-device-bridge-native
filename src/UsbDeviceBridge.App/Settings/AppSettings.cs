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

public static class UpdateCheckModes
{
    /// <summary>Check, download in background, prompt user to install.</summary>
    public const string Automatic = "automatic";

    /// <summary>Check and notify the user with a link to the release page; no download.</summary>
    public const string Notify = "notify";

    /// <summary>Disable update checks entirely.</summary>
    public const string Disabled = "disabled";

    public static readonly IReadOnlyList<string> All = [Automatic, Notify, Disabled];

    public static string GetLabel(string mode) => Normalize(mode) switch
    {
        Automatic => "Automatic install",
        Notify => "Notify only",
        Disabled => "Disabled",
        _ => "Automatic install",
    };

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Notify, StringComparison.OrdinalIgnoreCase))
            return Notify;
        if (string.Equals(value, Disabled, StringComparison.OrdinalIgnoreCase))
            return Disabled;
        return Automatic;
    }
}

public static class SshPortForwardModes
{
    public const string Disabled = "disabled";
    public const string Enabled = "enabled";

    public static readonly IReadOnlyList<string> All = [Enabled, Disabled];

    public static string Normalize(string? value)
    {
        if (string.Equals(value, Disabled, StringComparison.OrdinalIgnoreCase))
            return Disabled;

        return Enabled;
    }
}

public sealed class AppSettings
{
    public bool SetupCompleted { get; set; }

    public string Theme { get; set; } = "Dark";

    public bool MinimizeToTray { get; set; } = true;

    public bool StartMinimized { get; set; }

    public bool AutoRefreshEnabled { get; set; } = true;

    /// <summary>
    /// Controls how the app checks for new releases on GitHub.
    /// Accepted values: "automatic" (default — check, download, prompt to install),
    /// "notify" (check and notify with a link), "disabled" (no checks).
    /// </summary>
    public string UpdateCheckMode { get; set; } = UpdateCheckModes.Automatic;

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

    /// <summary>
    /// Controls whether SSH targets use a local port-forward tunnel before attach.
    /// Accepted values: "enabled" (default), "disabled".
    /// Invalid or missing values are normalized to "enabled".
    /// </summary>
    public string SshPortForwardMode { get; set; } = SshPortForwardModes.Enabled;

    /// <summary>
    /// User-defined SSH client aliases/hosts to include in target selection.
    /// These complement discovered entries from SSH config.
    /// </summary>
    public List<string> AdditionalSshClients { get; set; } = [];

    /// <summary>
    /// Last-used attach target (client dropdown value) per device, keyed by InstanceId.
    /// Persisted so the dropdown restores to the previous selection after app restart.
    /// </summary>
    public Dictionary<string, string> LastUsedClientByDevice { get; set; } = [];
}
