using UsbDeviceBridge.App.ViewModels;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Tests;

public sealed class DeviceSorterTests
{
    private static Device MakeDevice(string instanceId, string description, string state) => new()
    {
        InstanceId = instanceId,
        Description = description,
        State = state,
    };

    // ---------- "Name" sort ----------

    [Fact]
    public void Sort_ByName_OrdersAlphabetically()
    {
        var devices = new[]
        {
            MakeDevice("id3", "Zebra Cam", "available"),
            MakeDevice("id1", "Alpha Stick", "attached"),
            MakeDevice("id2", "Mouse", "offline"),
        };

        var result = DeviceSorter.Sort(devices, "Name");

        Assert.Equal(["Alpha Stick", "Mouse", "Zebra Cam"], result.Select(d => d.Description));
    }

    [Fact]
    public void Sort_ByName_IsCaseInsensitive()
    {
        var devices = new[]
        {
            MakeDevice("b", "banana", "available"),
            MakeDevice("a", "Apple", "available"),
        };

        var result = DeviceSorter.Sort(devices, "Name");

        Assert.Equal(["Apple", "banana"], result.Select(d => d.Description));
    }

    [Fact]
    public void Sort_ByName_TieBreaksOnInstanceId()
    {
        var devices = new[]
        {
            MakeDevice("id2", "Same Name", "available"),
            MakeDevice("id1", "Same Name", "offline"),
        };

        var result = DeviceSorter.Sort(devices, "Name");

        Assert.Equal(["id1", "id2"], result.Select(d => d.InstanceId));
    }

    // ---------- "State then name" sort ----------

    [Fact]
    public void Sort_ByStateThenName_OrdersAttachedFirst()
    {
        var devices = new[]
        {
            MakeDevice("a", "Alpha", "offline"),
            MakeDevice("b", "Beta", "available"),
            MakeDevice("c", "Gamma", "attached"),
            MakeDevice("d", "Delta", "shared"),
        };

        var result = DeviceSorter.Sort(devices, "State then name");

        Assert.Equal(["attached", "shared", "available", "offline"], result.Select(d => d.State));
    }

    [Fact]
    public void Sort_ByStateThenName_WithinSameState_OrdersAlphabetically()
    {
        var devices = new[]
        {
            MakeDevice("b", "Zebra", "available"),
            MakeDevice("a", "Alpha", "available"),
        };

        var result = DeviceSorter.Sort(devices, "State then name");

        Assert.Equal(["Alpha", "Zebra"], result.Select(d => d.Description));
    }

    [Fact]
    public void Sort_ByStateThenName_IsDefaultWhenUnrecognizedSortOrder()
    {
        var devices = new[]
        {
            MakeDevice("a", "Alpha", "offline"),
            MakeDevice("b", "Beta", "attached"),
        };

        var result = DeviceSorter.Sort(devices, "unknown-value");

        // Falls through to state-then-name
        Assert.Equal("attached", result[0].State);
        Assert.Equal("offline", result[1].State);
    }

    [Fact]
    public void Sort_ByStateThenName_UnknownStateRankedLast()
    {
        var devices = new[]
        {
            MakeDevice("a", "Alpha", "unknown-state"),
            MakeDevice("b", "Beta", "offline"),
            MakeDevice("c", "Gamma", "available"),
        };

        var result = DeviceSorter.Sort(devices, "State then name");

        Assert.Equal(["available", "offline", "unknown-state"], result.Select(d => d.State));
    }

    // ---------- GetStateRank ----------

    [Theory]
    [InlineData("attached", 0)]
    [InlineData("ATTACHED", 0)]
    [InlineData("shared", 1)]
    [InlineData("Shared", 1)]
    [InlineData("available", 2)]
    [InlineData("offline", 3)]
    [InlineData("unknown", 4)]
    [InlineData("", 4)]
    public void GetStateRank_ReturnsExpectedRank(string state, int expectedRank)
    {
        Assert.Equal(expectedRank, DeviceSorter.GetStateRank(state));
    }

    // ---------- Edge cases ----------

    [Fact]
    public void Sort_EmptyCollection_ReturnsEmpty()
    {
        var result = DeviceSorter.Sort([], "Name");
        Assert.Empty(result);
    }

    [Fact]
    public void Sort_SingleDevice_ReturnsSameDevice()
    {
        var device = MakeDevice("id1", "Only Device", "available");
        var result = DeviceSorter.Sort([device], "Name");
        Assert.Single(result);
        Assert.Equal("id1", result[0].InstanceId);
    }
}
