using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Service.Services;

public readonly record struct RememberedAttachTarget(
    string InstanceId,
    string BusId,
    string Distro,
    DeviceState State
);

public static class RememberedDeviceAutoAttachPlanner
{
    public static HashSet<string> ParseAvailableDistros(string stdout)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in WslDistroParser.ParseVerbose(stdout))
            result.Add(entry.Name);

        return result;
    }

    public static IReadOnlyList<RememberedAttachTarget> SelectAttachTargets(
        IReadOnlyDictionary<string, string> remembered,
        IReadOnlyList<UsbIpdStateDevice> devices,
        IReadOnlySet<string> availableDistros,
        IReadOnlyDictionary<string, DateTimeOffset> nextAttemptUtc,
        DateTimeOffset now
    )
    {
        var result = new List<RememberedAttachTarget>();

        foreach (var (instanceId, distro) in remembered)
        {
            if (string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(distro))
                continue;

            if (!availableDistros.Contains(distro))
                continue;

            if (nextAttemptUtc.TryGetValue(instanceId, out var nextAttempt) && nextAttempt > now)
                continue;

            var dev = devices.FirstOrDefault(
                d => string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase)
            );
            if (dev is null || string.IsNullOrWhiteSpace(dev.BusId))
                continue;

            var state = UsbIpdStateParser.Classify(dev);
            if (state is DeviceState.Attached or DeviceState.Offline)
                continue;

            result.Add(new RememberedAttachTarget(instanceId, dev.BusId, distro, state));
        }

        return result;
    }
}