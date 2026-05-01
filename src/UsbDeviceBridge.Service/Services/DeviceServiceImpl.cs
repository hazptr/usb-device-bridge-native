using Grpc.Core;
using Microsoft.Extensions.Hosting;
using UsbDeviceBridge.Service.Interop;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Services;

/// <summary>
/// Device operations moved to app process (BUG-0006 fix).
/// GetVersionInfo and WatchHeartbeat remain for settings display and connectivity.
/// </summary>
public sealed class DeviceServiceImpl : DeviceService.DeviceServiceBase
{
    private readonly ILogger<DeviceServiceImpl> _logger;
    private readonly VersionInfoProvider _versionInfoProvider;
    private readonly IHostApplicationLifetime _hostApplicationLifetime;

    public DeviceServiceImpl(
        ILogger<DeviceServiceImpl> logger,
        VersionInfoProvider versionInfoProvider,
        IHostApplicationLifetime hostApplicationLifetime)
    {
        _logger = logger;
        _versionInfoProvider = versionInfoProvider;
        _hostApplicationLifetime = hostApplicationLifetime;
    }

    public override async Task<Usbdevicebridge.V1.VersionInfo> GetVersionInfo(
        GetVersionInfoRequest request,
        ServerCallContext context)
    {
        try
        {
            var snapshot = await _versionInfoProvider.QueryAsync(context.CancellationToken);
            return new Usbdevicebridge.V1.VersionInfo
            {
                FrontendVersion = "N/A",
                ServiceVersion = snapshot.ServiceVersion,
                WslVersion = snapshot.WslVersion,
                UsbipdVersion = snapshot.UsbIpdVersion,
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetVersionInfo failed");
            throw new RpcException(new Status(StatusCode.Internal, ex.Message));
        }
    }

    public override async Task WatchHeartbeat(
        HeartbeatRequest request,
        IServerStreamWriter<HeartbeatEvent> responseStream,
        ServerCallContext context)
    {
        const int minIntervalMs = 500;
        const int maxIntervalMs = 10000;
        const int defaultIntervalMs = 2000;

        var requestedInterval = request.IntervalMs <= 0
            ? defaultIntervalMs
            : (int)request.IntervalMs;
        var intervalMs = Math.Clamp(requestedInterval, minIntervalMs, maxIntervalMs);

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.CancellationToken,
            _hostApplicationLifetime.ApplicationStopping);
        var cancellationToken = linkedCts.Token;

        ulong sequence = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = DateTimeOffset.UtcNow;
                await responseStream.WriteAsync(new HeartbeatEvent
                {
                    Sequence = sequence++,
                    UtcUnixMs = now.ToUnixTimeMilliseconds(),
                });

                await Task.Delay(intervalMs, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during service shutdown or client disconnect.
        }
    }
}
