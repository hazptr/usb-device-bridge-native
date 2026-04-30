using Grpc.Core;
using UsbDeviceBridge.Service.Domain;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Services;

public sealed class AutoAttachServiceImpl(
    ILogger<AutoAttachServiceImpl> logger,
    RememberedDeviceStore rememberedDeviceStore,
    AutoAttachAttemptCancellationRegistry autoAttachAttemptCancellationRegistry
) : AutoAttachService.AutoAttachServiceBase
{
    public override Task<RememberDeviceResponse> RememberDevice(
        RememberDeviceRequest request,
        ServerCallContext context
    )
    {
        if (string.IsNullOrEmpty(request.InstanceId))
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "InstanceId is required.")
            );

        rememberedDeviceStore.AddOrUpdate(request.InstanceId, request.PreferredDistro ?? "");
        logger.LogInformation(
            "Remembered {InstanceId} → distro={Distro}",
            request.InstanceId,
            request.PreferredDistro
        );

        return Task.FromResult(new RememberDeviceResponse { Ok = true, Message = "Device remembered." });
    }

    public override Task<ForgetDeviceResponse> ForgetDevice(
        ForgetDeviceRequest request,
        ServerCallContext context
    )
    {
        if (string.IsNullOrEmpty(request.InstanceId))
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "InstanceId is required.")
            );

        var removed = rememberedDeviceStore.Remove(request.InstanceId);
        var canceledAttempt = autoAttachAttemptCancellationRegistry.Cancel(request.InstanceId);
        logger.LogInformation(
            "Forget {InstanceId}: removed={Removed} canceledAttempt={CanceledAttempt}",
            request.InstanceId,
            removed,
            canceledAttempt
        );

        return Task.FromResult(new ForgetDeviceResponse
        {
            Ok = true,
            Message = removed ? "Device forgotten." : "Device was not remembered.",
        });
    }

    public override Task<GetRememberedDevicesResponse> GetRememberedDevices(
        GetRememberedDevicesRequest request,
        ServerCallContext context
    )
    {
        var entries = rememberedDeviceStore.Load();
        var response = new GetRememberedDevicesResponse();

        foreach (var (instanceId, distro) in entries)
        {
            response.Devices.Add(
                new RememberedDevice { InstanceId = instanceId, PreferredDistro = distro }
            );
        }

        return Task.FromResult(response);
    }
}
