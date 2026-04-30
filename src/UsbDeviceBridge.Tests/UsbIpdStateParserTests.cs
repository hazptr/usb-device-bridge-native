using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Tests;

public class UsbIpdStateParserTests
{
    private const string SampleJson = """
        {
          "Devices": [
            {
              "BusId": "1-1",
              "ClientIPAddress": null,
              "Description": "USB Keyboard",
              "InstanceId": "USB\\VID_046D&PID_C31C\\7&342ACA47&0&1",
              "PersistedGuid": null,
              "StubInstanceId": "USBIP\\VID_046D&PID_C31C\\6"
            },
            {
              "BusId": "2-1",
              "ClientIPAddress": "172.17.0.1",
              "Description": "USB Mouse",
              "InstanceId": "USB\\VID_046D&PID_B01B\\5&38DC3C3&0&4",
              "PersistedGuid": null,
              "StubInstanceId": "USBIP\\VID_046D&PID_B01B\\5"
            },
            {
              "BusId": "3-2",
              "ClientIPAddress": null,
              "Description": "USB Drive",
              "InstanceId": "USB\\VID_0781&PID_5583\\AA12345678",
              "PersistedGuid": null,
              "StubInstanceId": null
            },
            {
              "BusId": null,
              "ClientIPAddress": null,
              "Description": "Offline Device",
              "InstanceId": "USB\\VID_1234&PID_5678\\DEADBEEF",
              "PersistedGuid": "12345678-1234-1234-1234-123456789012",
              "StubInstanceId": null
            }
          ]
        }
        """;

    [Fact]
    public void Parse_ValidJson_ReturnsAllDevices()
    {
        var (devices, error) = UsbIpdStateParser.Parse(SampleJson);

        Assert.Null(error);
        Assert.Equal(4, devices.Count);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsError()
    {
        var (devices, error) = UsbIpdStateParser.Parse("not json");

        Assert.NotNull(error);
        Assert.Empty(devices);
    }

    [Fact]
    public void Parse_MissingDevicesKey_ReturnsError()
    {
        var (devices, error) = UsbIpdStateParser.Parse("""{"other":"value"}""");

        Assert.NotNull(error);
        Assert.Empty(devices);
    }

    [Fact]
    public void Classify_ClientIPAddress_ReturnsAttached()
    {
        var dev = new UsbIpdStateDevice
        {
            BusId = "2-1",
            ClientIPAddress = "172.17.0.1",
            StubInstanceId = "USBIP\\...",
        };
        Assert.Equal(DeviceState.Attached, UsbIpdStateParser.Classify(dev));
    }

    [Fact]
    public void Classify_StubInstanceId_ReturnsShared()
    {
        var dev = new UsbIpdStateDevice
        {
            BusId = "1-1",
            ClientIPAddress = null,
            StubInstanceId = "USBIP\\...",
        };
        Assert.Equal(DeviceState.Shared, UsbIpdStateParser.Classify(dev));
    }

    [Fact]
    public void Classify_BusIdOnly_ReturnsAvailable()
    {
        var dev = new UsbIpdStateDevice
        {
            BusId = "3-2",
            ClientIPAddress = null,
            StubInstanceId = null,
        };
        Assert.Equal(DeviceState.Available, UsbIpdStateParser.Classify(dev));
    }

    [Fact]
    public void Classify_NoBusId_ReturnsOffline()
    {
        var dev = new UsbIpdStateDevice
        {
            BusId = null,
            ClientIPAddress = null,
            StubInstanceId = null,
        };
        Assert.Equal(DeviceState.Offline, UsbIpdStateParser.Classify(dev));
    }

    [Fact]
    public void Classify_WhitespaceFields_ReturnsOffline()
    {
        var dev = new UsbIpdStateDevice
        {
            BusId = " ",
            ClientIPAddress = " ",
            StubInstanceId = "\t",
        };

        Assert.Equal(DeviceState.Offline, UsbIpdStateParser.Classify(dev));
    }

    [Fact]
    public void Classify_AllDevicesFromSample_CorrectStates()
    {
        var (devices, _) = UsbIpdStateParser.Parse(SampleJson);

        Assert.Equal(DeviceState.Shared, UsbIpdStateParser.Classify(devices[0]));
        Assert.Equal(DeviceState.Attached, UsbIpdStateParser.Classify(devices[1]));
        Assert.Equal(DeviceState.Available, UsbIpdStateParser.Classify(devices[2]));
        Assert.Equal(DeviceState.Offline, UsbIpdStateParser.Classify(devices[3]));
    }

    [Theory]
    [InlineData("USB\\VID_046D&PID_C31C\\7&342ACA47&0&1", "046d:c31c")]
    [InlineData("USB\\VID_0781&PID_5583\\AA12345678", "0781:5583")]
    [InlineData("USB\\VID_1234&PID_5678\\DEADBEEF", "1234:5678")]
    public void ExtractVidPid_ValidInstanceId_ReturnsLowercase(
        string instanceId,
        string expected
    )
    {
        Assert.Equal(expected, UsbIpdStateParser.ExtractVidPid(instanceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ACPI\\PNP0501\\1")]
    [InlineData("USB\\VID_046D")]
    public void ExtractVidPid_InvalidOrMissing_ReturnsNull(string? instanceId)
    {
        Assert.Null(UsbIpdStateParser.ExtractVidPid(instanceId));
    }

    [Fact]
    public void Parse_EmptyDevicesArray_ReturnsEmptyList()
    {
        var (devices, error) = UsbIpdStateParser.Parse("""{"Devices":[]}""");

        Assert.Null(error);
        Assert.Empty(devices);
    }
}
