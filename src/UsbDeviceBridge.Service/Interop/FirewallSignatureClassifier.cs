namespace UsbDeviceBridge.Service.Interop;

/// <summary>
/// Classifies usbipd attach output to determine whether the failure is likely
/// caused by a Windows Firewall / Public-profile block on the WSL vEthernet adapter.
/// Matches the same signature set as the Python reference implementation.
/// </summary>
public static class FirewallSignatureClassifier
{
    // Markers that indicate a firewall/network-policy block in usbipd output.
    // "timed out" is checked separately (case-insensitive).
    private static readonly string[] Markers =
    [
        "firewall",
        "3240",
        "group policy",
        "public network profile",
        "blocking the connection",
    ];

    /// <summary>
    /// Returns <c>true</c> when <paramref name="output"/> contains at least one
    /// signature that suggests the attach failure is caused by a firewall or
    /// network-policy block rather than a device or distro problem.
    /// </summary>
    public static bool IsFirewallBlock(string? output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        var lower = output.ToLowerInvariant();

        // "timed out" is the single highest-signal marker.
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
