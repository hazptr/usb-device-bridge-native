using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Service.Devices;

/// <summary>
/// Static helpers for mapping <see cref="UsbIpdStateDevice"/> to protocol/hardware identifiers.
/// </summary>
internal static class DeviceMapper
{
    /// <summary>
    /// Builds a colon-separated VID:PID hardware identifier from raw device data,
    /// or returns <see langword="null"/> if either VID or PID is absent.
    /// </summary>
    internal static string? BuildHardwareId(UsbIpdStateDevice source)
    {
        if (source.VendorId is null || source.ProductId is null)
            return null;

        return $"{source.VendorId.Value:x4}:{source.ProductId.Value:x4}";
    }
}
