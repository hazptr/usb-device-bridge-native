using UsbDeviceBridge.Service.Interop;
using UsbDeviceBridge.Service.Services;

namespace UsbDeviceBridge.Tests;

public class RememberedDeviceAutoAttachPlannerTests
{
    [Fact]
    public void ParseAvailableDistros_ReturnsOnlyRunningDistros()
    {
        var stdout = "NAME                   STATE           VERSION\n* Ubuntu-24.04         Running         2\n  Debian               Stopped         2\n  Ubuntu Dev           Running         2\n";

        var distros = RememberedDeviceAutoAttachPlanner.ParseAvailableDistros(stdout);

        Assert.Equal(2, distros.Count);
        Assert.Contains("Ubuntu-24.04", distros);
        Assert.Contains("Ubuntu Dev", distros);
        Assert.DoesNotContain("Debian", distros);
    }

    [Fact]
    public void SelectAttachTargets_ReturnsOnlyEligibleRememberedDevices()
    {
        var remembered = new Dictionary<string, string>
        {
            ["dev-1"] = "Ubuntu",
            ["dev-2"] = "Debian",
            ["dev-3"] = "MissingDistro",
        };

        var devices = new[]
        {
            BuildDevice("dev-1", "1-1", state: DeviceState.Available),
            BuildDevice("dev-2", "2-1", state: DeviceState.Attached),
            BuildDevice("dev-3", "3-1", state: DeviceState.Shared),
        };

        var distros = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Ubuntu", "Debian" };
        var nextAttempt = new Dictionary<string, DateTimeOffset>();

        var targets = RememberedDeviceAutoAttachPlanner.SelectAttachTargets(
            remembered,
            devices,
            distros,
            nextAttempt,
            DateTimeOffset.UtcNow
        );

        var target = Assert.Single(targets);
        Assert.Equal("dev-1", target.InstanceId);
        Assert.Equal("1-1", target.BusId);
        Assert.Equal("Ubuntu", target.Distro);
        Assert.Equal(DeviceState.Available, target.State);
    }

    [Fact]
    public void SelectAttachTargets_SkipsDevicesUntilThrottleExpires()
    {
        var remembered = new Dictionary<string, string> { ["dev-1"] = "Ubuntu" };
        var devices = new[] { BuildDevice("dev-1", "1-1", state: DeviceState.Shared) };
        var distros = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ubuntu" };
        var now = DateTimeOffset.UtcNow;

        var blocked = RememberedDeviceAutoAttachPlanner.SelectAttachTargets(
            remembered,
            devices,
            distros,
            new Dictionary<string, DateTimeOffset> { ["dev-1"] = now.AddSeconds(30) },
            now
        );
        Assert.Empty(blocked);

        var allowed = RememberedDeviceAutoAttachPlanner.SelectAttachTargets(
            remembered,
            devices,
            distros,
            new Dictionary<string, DateTimeOffset> { ["dev-1"] = now.AddSeconds(-1) },
            now
        );
        Assert.Single(allowed);
    }

    private static UsbIpdStateDevice BuildDevice(string instanceId, string busId, DeviceState state)
    {
        return state switch
        {
            DeviceState.Available => new UsbIpdStateDevice
            {
                InstanceId = instanceId,
                BusId = busId,
                StubInstanceId = null,
                ClientIPAddress = null,
            },
            DeviceState.Shared => new UsbIpdStateDevice
            {
                InstanceId = instanceId,
                BusId = busId,
                StubInstanceId = "USBIP\\STUB",
                ClientIPAddress = null,
            },
            DeviceState.Attached => new UsbIpdStateDevice
            {
                InstanceId = instanceId,
                BusId = busId,
                StubInstanceId = "USBIP\\STUB",
                ClientIPAddress = "172.20.0.1",
            },
            _ => new UsbIpdStateDevice
            {
                InstanceId = instanceId,
                BusId = null,
                StubInstanceId = null,
                ClientIPAddress = null,
            },
        };
    }
}