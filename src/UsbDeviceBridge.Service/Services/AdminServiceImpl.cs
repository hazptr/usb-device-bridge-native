using Grpc.Core;
using UsbDeviceBridge.Service.Interop;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Services;

/// <summary>
/// Privileged operations that require SYSTEM/admin access.
/// Bind, unbind, and firewall-fix stay in the service; all user-context
/// operations (attach, detach, device list, auto-attach) moved to the app.
/// </summary>
public sealed class AdminServiceImpl : AdminService.AdminServiceBase
{
    private readonly ILogger<AdminServiceImpl> _logger;
    private readonly UsbIpdClient _usbIpdClient;

    public AdminServiceImpl(ILogger<AdminServiceImpl> logger, UsbIpdClient usbIpdClient)
    {
        _logger = logger;
        _usbIpdClient = usbIpdClient;
    }

    public override async Task<BindDeviceResponse> BindDevice(
        BindDeviceRequest request,
        ServerCallContext context)
    {
        var busId = request.BusId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(busId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "BusId is required."));

        _logger.LogInformation("Bind requested bus={BusId} force={Force}", busId, request.Force);

        var (ok, msg) = await _usbIpdClient.BindAsync(busId, request.Force, context.CancellationToken);
        return new BindDeviceResponse
        {
            Ok = ok,
            Message = msg.Length > 0 ? msg : (ok ? "Bound." : "Bind failed."),
        };
    }

    public override async Task<UnbindDeviceResponse> UnbindDevice(
        UnbindDeviceRequest request,
        ServerCallContext context)
    {
        var busId = request.BusId?.Trim() ?? string.Empty;
        var hardwareId = request.HardwareId?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(busId) && string.IsNullOrEmpty(hardwareId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "BusId or HardwareId is required."));

        _logger.LogInformation("Unbind requested bus={BusId} hwid={HardwareId}", busId, hardwareId);

        // Try bus-id first (most precise).
        if (!string.IsNullOrEmpty(busId))
        {
            var (ok, msg) = await _usbIpdClient.UnbindAsync(busId, context.CancellationToken);
            if (ok)
                return new UnbindDeviceResponse { Ok = true, Message = "Unbound." };

            _logger.LogDebug("Unbind by bus-id failed: {Message}", msg);
        }

        // Fall back to hardware-id (stable across re-enumeration).
        if (!string.IsNullOrEmpty(hardwareId))
        {
            var (ok, msg) = await _usbIpdClient.UnbindByHardwareIdAsync(hardwareId, context.CancellationToken);
            if (ok)
                return new UnbindDeviceResponse { Ok = true, Message = "Unbound by hardware ID." };

            _logger.LogDebug("Unbind by hardware-id failed: {Message}", msg);
            return new UnbindDeviceResponse
            {
                Ok = false,
                Message = msg.Length > 0 ? msg : "Unbind failed.",
            };
        }

        // Bus-id failed and no hardware-id was provided.
        return new UnbindDeviceResponse { Ok = false, Message = "Unbind by bus-id failed and no hardware ID available." };
    }

    public override async Task<ApplyFirewallFixResponse> ApplyFirewallFix(
        ApplyFirewallFixRequest request,
        ServerCallContext context)
    {
        _logger.LogInformation("Firewall fix requested by app");

        var (status, detail) = await WslFirewallFixer.ApplyAndVerifyAsync(
            _logger, context.CancellationToken);

        return new ApplyFirewallFixResponse
        {
            Ok = status == WslFirewallFixer.FixStatus.Ok,
            Message = detail.Length > 0
                ? detail
                : (status == WslFirewallFixer.FixStatus.Ok ? "Firewall fix applied." : "Firewall fix failed."),
        };
    }
}
