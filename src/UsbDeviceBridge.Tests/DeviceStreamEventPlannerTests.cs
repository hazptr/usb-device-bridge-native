using UsbDeviceBridge.Service.Services;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Tests;

public class DeviceStreamEventPlannerTests
{
    [Fact]
    public void Plan_WhenDeviceAdded_EmitsAdded()
    {
        var previous = new Dictionary<string, Device>();
        var current = new[] { BuildDevice("dev-1", "1-1", "available") };

        var result = DeviceStreamEventPlanner.Plan(previous, current);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal("added", delta.EventType);
        Assert.Equal("instance:dev-1", delta.Key);
    }

    [Fact]
    public void Plan_WhenDeviceChanges_EmitsChanged()
    {
        var existing = BuildDevice("dev-1", "1-1", "available");
        var previous = DeviceStreamEventPlanner.BuildSnapshot(new[] { existing });
        var current = new[] { BuildDevice("dev-1", "1-1", "attached") };

        var result = DeviceStreamEventPlanner.Plan(previous, current);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal("changed", delta.EventType);
        Assert.Equal("attached", delta.Device.State);
    }

    [Fact]
    public void Plan_WhenDeviceRemoved_EmitsRemoved()
    {
        var existing = BuildDevice("dev-1", "1-1", "available");
        var previous = DeviceStreamEventPlanner.BuildSnapshot(new[] { existing });

        var result = DeviceStreamEventPlanner.Plan(previous, []);

        var delta = Assert.Single(result.Deltas);
        Assert.Equal("removed", delta.EventType);
        Assert.Equal("dev-1", delta.Device.InstanceId);
    }

    [Fact]
    public void BuildSnapshot_WhenFallbackKeyCollides_UsesDuplicateSuffix()
    {
        var d1 = BuildDevice("", "", "available", hardwareId: "1234:5678", description: "USB Device");
        var d2 = BuildDevice("", "", "shared", hardwareId: "1234:5678", description: "USB Device");

        var snapshot = DeviceStreamEventPlanner.BuildSnapshot(new[] { d1, d2 });

        Assert.Equal(2, snapshot.Count);
        Assert.Contains("meta:1234:5678|USB Device", snapshot.Keys);
        Assert.Contains("meta:1234:5678|USB Device:dup:1", snapshot.Keys);
    }

    [Fact]
    public void Merge_WhenAddedThenChanged_StaysAddedWithLatestDevice()
    {
        var added = new DeviceStreamEventPlanner.DeviceDelta(
            "instance:dev-1",
            "added",
            BuildDevice("dev-1", "1-1", "available")
        );
        var changed = new DeviceStreamEventPlanner.DeviceDelta(
            "instance:dev-1",
            "changed",
            BuildDevice("dev-1", "1-1", "attached")
        );

        var merged = DeviceStreamEventPlanner.Merge(added, changed);

        Assert.Equal("added", merged.EventType);
        Assert.Equal("attached", merged.Device.State);
    }

    [Fact]
    public void Clone_ProducesNonNullStringFields()
    {
        var source = new Device();

        var cloned = DeviceStreamEventPlanner.Clone(source);

        Assert.Equal(string.Empty, cloned.InstanceId);
        Assert.Equal(string.Empty, cloned.BusId);
        Assert.Equal(string.Empty, cloned.Description);
        Assert.Equal(string.Empty, cloned.HardwareId);
        Assert.Equal(string.Empty, cloned.State);
        Assert.Equal(string.Empty, cloned.PreferredDistro);
    }

    private static Device BuildDevice(
        string instanceId,
        string busId,
        string state,
        string hardwareId = "046d:c31c",
        string description = "USB Keyboard"
    ) =>
        new()
        {
            InstanceId = instanceId,
            BusId = busId,
            State = state,
            HardwareId = hardwareId,
            Description = description,
            PreferredDistro = "Ubuntu",
            Remembered = false,
            Attaching = false,
        };
}
