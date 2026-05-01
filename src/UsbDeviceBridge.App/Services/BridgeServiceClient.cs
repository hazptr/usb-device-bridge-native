using Grpc.Net.Client;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

public sealed class BridgeServiceClient : IDisposable
{
    private readonly GrpcChannel _channel;

    public BridgeServiceClient(string serviceAddress)
    {
        _channel = GrpcChannel.ForAddress(serviceAddress);
        Admin = new AdminService.AdminServiceClient(_channel);
        Device = new DeviceService.DeviceServiceClient(_channel);
        Setup = new SetupService.SetupServiceClient(_channel);
    }

    /// <summary>Privileged operations: bind, unbind, firewall fix.</summary>
    public AdminService.AdminServiceClient Admin { get; }

    /// <summary>Version info only; device RPCs return Unimplemented (moved to app).</summary>
    public DeviceService.DeviceServiceClient Device { get; }

    public SetupService.SetupServiceClient Setup { get; }

    public void Dispose()
    {
        _channel.Dispose();
    }
}
