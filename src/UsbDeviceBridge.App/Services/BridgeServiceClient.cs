using Grpc.Net.Client;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

public sealed class BridgeServiceClient : IDisposable
{
    private readonly GrpcChannel _channel;

    public BridgeServiceClient(string serviceAddress)
    {
        _channel = GrpcChannel.ForAddress(serviceAddress);
        Device = new DeviceService.DeviceServiceClient(_channel);
        AutoAttach = new AutoAttachService.AutoAttachServiceClient(_channel);
        Setup = new SetupService.SetupServiceClient(_channel);
    }

    public DeviceService.DeviceServiceClient Device { get; }

    public AutoAttachService.AutoAttachServiceClient AutoAttach { get; }

    public SetupService.SetupServiceClient Setup { get; }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
