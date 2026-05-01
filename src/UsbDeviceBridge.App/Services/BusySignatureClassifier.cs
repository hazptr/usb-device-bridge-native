namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Detects usbipd/usbip busy-device failures that explicitly advertise
/// a force retry path.
/// </summary>
public static class BusySignatureClassifier
{
    private static readonly string[] BusyMarkers =
    [
        "device busy",
        "used by windows",
        "busy (exported)",
    ];

    private static readonly string[] ForceMarkers =
    [
        "--force",
        "force option",
    ];

    public static bool IsBusyWithForceAvailable(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return false;

        var lower = output.ToLowerInvariant();

        var hasBusyMarker = false;
        foreach (var marker in BusyMarkers)
        {
            if (!lower.Contains(marker, StringComparison.Ordinal))
                continue;

            hasBusyMarker = true;
            break;
        }

        if (!hasBusyMarker)
            return false;

        foreach (var marker in ForceMarkers)
        {
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
