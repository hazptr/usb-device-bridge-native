using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.ViewModels;

public static class DeviceSorter
{
    public static List<Device> Sort(IEnumerable<Device> devices, string sortOrder)
    {
        if (string.Equals(sortOrder, "Name", StringComparison.OrdinalIgnoreCase))
        {
            return devices
                .OrderBy(d => d.Description, StringComparer.OrdinalIgnoreCase)
                .ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return devices
            .OrderBy(d => GetStateRank(d.State))
            .ThenBy(d => d.Description, StringComparer.OrdinalIgnoreCase)
            .ThenBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int GetStateRank(string state) => state.ToLowerInvariant() switch
    {
        "attached" => 0,
        "shared" => 1,
        "available" => 2,
        "offline" => 3,
        _ => 4,
    };
}
