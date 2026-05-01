using System.Buffers.Binary;
using System.Text;

namespace UsbDeviceBridge.App.Interop.UsbIpProtocol;

public static class UsbIpPacketCodec
{
    public const ushort ProtocolVersion = 0x0111;
    public const int CommonHeaderLength = 8;
    public const int ExportedDeviceRecordLength = 312;
    public const int InterfaceRecordLength = 4;

    private const int PathFieldLength = 256;
    private const int BusIdFieldLength = 32;

    public static byte[] BuildDevListRequest()
    {
        var buffer = new byte[CommonHeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(0, 2), ProtocolVersion);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2, 2), UsbIpCodes.OpReqDevList);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4, 4), 0);
        return buffer;
    }

    public static bool TryParseCommonHeader(ReadOnlySpan<byte> data, out UsbIpCommonHeader header)
    {
        if (data.Length < CommonHeaderLength)
        {
            header = default;
            return false;
        }

        var version = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(0, 2));
        var code = BinaryPrimitives.ReadUInt16BigEndian(data.Slice(2, 2));
        var status = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(4, 4));
        header = new UsbIpCommonHeader(version, code, status);
        return true;
    }

    public static bool TryGetDeviceCount(UsbIpCommonHeader header, out int deviceCount)
    {
        if (header.Status > int.MaxValue)
        {
            deviceCount = 0;
            return false;
        }

        deviceCount = (int)header.Status;
        return true;
    }

    public static bool TryParseExportedDeviceRecord(
        ReadOnlySpan<byte> data,
        out UsbIpExportedDevice device
    )
    {
        if (data.Length < ExportedDeviceRecordLength)
        {
            device = default;
            return false;
        }

        var busId = ReadNullTerminatedAscii(data.Slice(PathFieldLength, BusIdFieldLength));
        var busNumber = BinaryPrimitives.ReadUInt32BigEndian(data.Slice(PathFieldLength + BusIdFieldLength, 4));
        var deviceNumber = BinaryPrimitives.ReadUInt32BigEndian(
            data.Slice(PathFieldLength + BusIdFieldLength + 4, 4)
        );
        var vendorId = BinaryPrimitives.ReadUInt16BigEndian(
            data.Slice(PathFieldLength + BusIdFieldLength + 12, 2)
        );
        var productId = BinaryPrimitives.ReadUInt16BigEndian(
            data.Slice(PathFieldLength + BusIdFieldLength + 14, 2)
        );
        var deviceClass = data[PathFieldLength + BusIdFieldLength + 18];
        var interfaceCount = data[PathFieldLength + BusIdFieldLength + 23];

        device = new UsbIpExportedDevice(
            BusId: busId,
            DeviceId: $"{busNumber}-{deviceNumber}",
            VendorId: vendorId,
            ProductId: productId,
            DeviceClass: deviceClass,
            InterfaceCount: interfaceCount
        );

        return true;
    }

    private static string ReadNullTerminatedAscii(ReadOnlySpan<byte> field)
    {
        var end = field.IndexOf((byte)0);
        if (end < 0)
            end = field.Length;

        return Encoding.ASCII.GetString(field.Slice(0, end)).Trim();
    }
}

public readonly record struct UsbIpExportedDevice(
    string BusId,
    string DeviceId,
    ushort VendorId,
    ushort ProductId,
    byte DeviceClass,
    byte InterfaceCount
);
