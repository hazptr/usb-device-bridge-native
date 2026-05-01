namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Classifies usbipd attach output to determine whether the failure is likely
/// caused by a Windows Firewall / Public-profile block on the WSL vEthernet adapter.
/// </summary>
public static class FirewallSignatureClassifier
{
    private static readonly string[] Markers =
    [
        "firewall",
        "3240",
        "group policy",
        "public network profile",
        "blocking the connection",
    ];

    public static bool IsFirewallBlock(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        var lower = output.ToLowerInvariant();

        if (lower.Contains("timed out"))
            return true;

        foreach (var marker in Markers)
        {
            if (lower.Contains(marker))
                return true;
        }

        return false;
    }
}
