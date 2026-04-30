namespace UsbDeviceBridge.Service.Interop.UsbIpProtocol;

public readonly record struct UsbIpCommonHeader(ushort Version, ushort Code, uint Status);

public static class UsbIpCodes
{
    public const ushort OpReqDevList = 0x8005;
    public const ushort OpRepDevList = 0x0005;
    public const ushort OpReqImport = 0x8003;
    public const ushort OpRepImport = 0x0003;
}
