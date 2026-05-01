using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Services;

public static class DeviceStreamEventPlanner
{
    public sealed record DeviceDelta(string Key, string EventType, Device Device);

    public sealed record PlanResult(
        IReadOnlyList<DeviceDelta> Deltas,
        IReadOnlyDictionary<string, Device> Snapshot
    );

    public static PlanResult Plan(
        IReadOnlyDictionary<string, Device> previous,
        IEnumerable<Device> currentDevices
    )
    {
        var current = BuildSnapshot(currentDevices);
        var deltas = new List<DeviceDelta>();

        foreach (var entry in current)
        {
            if (!previous.TryGetValue(entry.Key, out var previousDevice))
            {
                deltas.Add(new DeviceDelta(entry.Key, "added", Clone(entry.Value)));
                continue;
            }

            if (!SameDevice(previousDevice, entry.Value))
            {
                deltas.Add(new DeviceDelta(entry.Key, "changed", Clone(entry.Value)));
            }
        }

        foreach (var removedKey in previous.Keys.Except(current.Keys))
        {
            deltas.Add(new DeviceDelta(removedKey, "removed", Clone(previous[removedKey])));
        }

        return new PlanResult(deltas, current);
    }

    public static Dictionary<string, Device> BuildSnapshot(IEnumerable<Device> devices)
    {
        var snapshot = new Dictionary<string, Device>(StringComparer.Ordinal);
        var keyCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var device in devices)
        {
            var baseKey = BuildBaseKey(device);
            keyCounts.TryGetValue(baseKey, out var seenCount);
            keyCounts[baseKey] = seenCount + 1;

            var key = seenCount == 0 ? baseKey : $"{baseKey}:dup:{seenCount}";
            snapshot[key] = Clone(device);
        }

        return snapshot;
    }

    public static string BuildBaseKey(Device device)
    {
        var instanceId = NormalizeString(device.InstanceId);
        var busId = NormalizeString(device.BusId);
        var hardwareId = NormalizeString(device.HardwareId);
        var description = NormalizeString(device.Description);

        if (!string.IsNullOrWhiteSpace(instanceId))
            return $"instance:{instanceId}";
        if (!string.IsNullOrWhiteSpace(busId))
            return $"bus:{busId}";

        return $"meta:{hardwareId}|{description}";
    }

    public static bool SameDevice(Device a, Device b) =>
        NormalizeString(a.InstanceId) == NormalizeString(b.InstanceId) &&
        NormalizeString(a.BusId) == NormalizeString(b.BusId) &&
        NormalizeString(a.Description) == NormalizeString(b.Description) &&
        NormalizeString(a.HardwareId) == NormalizeString(b.HardwareId) &&
        NormalizeString(a.State) == NormalizeString(b.State) &&
        a.Remembered == b.Remembered &&
        NormalizeString(a.PreferredDistro) == NormalizeString(b.PreferredDistro) &&
        NormalizeTarget(a.Target).Equals(NormalizeTarget(b.Target)) &&
        a.Attaching == b.Attaching;

    public static Device Clone(Device source) =>
        new()
        {
            InstanceId = NormalizeString(source.InstanceId),
            BusId = NormalizeString(source.BusId),
            Description = NormalizeString(source.Description),
            HardwareId = NormalizeString(source.HardwareId),
            State = NormalizeString(source.State),
            Remembered = source.Remembered,
            PreferredDistro = NormalizeString(source.PreferredDistro),
            Target = new AttachTarget
            {
                Type = NormalizeTarget(source.Target).Type,
                Name = NormalizeTarget(source.Target).Name,
            },
            Attaching = source.Attaching,
        };

    private static string NormalizeString(string? value) => value ?? string.Empty;

    private static AttachTarget NormalizeTarget(AttachTarget? target)
        => new()
        {
            Type = target?.Type is AttachTargetType.Ssh
                ? AttachTargetType.Ssh
                : AttachTargetType.Wsl,
            Name = NormalizeString(target?.Name),
        };

    public static DeviceDelta Merge(DeviceDelta existing, DeviceDelta incoming)
    {
        if (incoming.EventType == "removed")
            return incoming;

        if (existing.EventType == "added" && incoming.EventType == "changed")
        {
            return new DeviceDelta(existing.Key, "added", Clone(incoming.Device));
        }

        return incoming;
    }
}

