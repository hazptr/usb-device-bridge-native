using UsbDeviceBridge.Service.Interop.UsbIpProtocol;

namespace UsbDeviceBridge.Tests;

public class UsbIpPacketCodecTests
{
    [Fact]
    public void BuildDevListRequest_WritesExpectedHeader()
    {
        var payload = UsbIpPacketCodec.BuildDevListRequest();

        Assert.Equal(8, payload.Length);
        Assert.Equal(0x01, payload[0]);
        Assert.Equal(0x11, payload[1]);
        Assert.Equal(0x80, payload[2]);
        Assert.Equal(0x05, payload[3]);
        Assert.Equal(0x00, payload[4]);
        Assert.Equal(0x00, payload[5]);
        Assert.Equal(0x00, payload[6]);
        Assert.Equal(0x00, payload[7]);
    }

    [Fact]
    public void TryParseCommonHeader_ParsesBigEndianValues()
    {
        byte[] payload = [0x01, 0x11, 0x00, 0x05, 0x00, 0x00, 0x00, 0x02];

        var ok = UsbIpPacketCodec.TryParseCommonHeader(payload, out var header);

        Assert.True(ok);
        Assert.Equal(0x0111, header.Version);
        Assert.Equal(UsbIpCodes.OpRepDevList, header.Code);
        Assert.Equal((uint)2, header.Status);
    }

    [Fact]
    public void TryParseCommonHeader_ReturnsFalseOnShortPayload()
    {
        byte[] payload = [0x01, 0x11, 0x00, 0x05];

        var ok = UsbIpPacketCodec.TryParseCommonHeader(payload, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryGetDeviceCount_ReturnsStatusAsCount()
    {
        var header = new UsbIpCommonHeader(
            UsbIpPacketCodec.ProtocolVersion,
            UsbIpCodes.OpRepDevList,
            3
        );

        var ok = UsbIpPacketCodec.TryGetDeviceCount(header, out var count);

        Assert.True(ok);
        Assert.Equal(3, count);
    }

    [Fact]
    public void TryParseExportedDeviceRecord_ParsesMetadata()
    {
        var payload = new byte[UsbIpPacketCodec.ExportedDeviceRecordLength];

        WriteAscii(payload, 256, 32, "1-3");
        WriteUInt32BigEndian(payload, 288, 1);
        WriteUInt32BigEndian(payload, 292, 3);
        WriteUInt16BigEndian(payload, 300, 0x046D);
        WriteUInt16BigEndian(payload, 302, 0xC31C);
        payload[306] = 0x03;
        payload[311] = 2;

        var ok = UsbIpPacketCodec.TryParseExportedDeviceRecord(payload, out var device);

        Assert.True(ok);
        Assert.Equal("1-3", device.BusId);
        Assert.Equal("1-3", device.DeviceId);
        Assert.Equal((ushort)0x046D, device.VendorId);
        Assert.Equal((ushort)0xC31C, device.ProductId);
        Assert.Equal((byte)0x03, device.DeviceClass);
        Assert.Equal((byte)2, device.InterfaceCount);
    }

    private static void WriteUInt16BigEndian(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)(value >> 8);
        target[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteUInt32BigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)((value >> 16) & 0xFF);
        target[offset + 2] = (byte)((value >> 8) & 0xFF);
        target[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteAscii(byte[] target, int offset, int fieldLength, string value)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(value);
        Array.Copy(bytes, 0, target, offset, Math.Min(bytes.Length, fieldLength));
    }
}
