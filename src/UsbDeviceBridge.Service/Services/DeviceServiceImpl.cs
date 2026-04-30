using Grpc.Core;
using UsbDeviceBridge.Service.Devices;
using UsbDeviceBridge.Service.Domain;
using UsbDeviceBridge.Service.Interop;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Services;

public sealed class DeviceServiceImpl : DeviceService.DeviceServiceBase
{
    private readonly ILogger<DeviceServiceImpl> logger;
    private readonly UsbIpdClient usbIpdClient;
    private readonly WslInterop wslInterop;
    private readonly RememberedDeviceStore rememberedDeviceStore;
    private readonly AutoAttachActivityTracker autoAttachActivityTracker;
    private readonly ServiceClientConnectionTracker connectionTracker;
    private readonly AttachConfirmationPoller _confirmationPoller;

    public DeviceServiceImpl(
        ILogger<DeviceServiceImpl> logger,
        UsbIpdClient usbIpdClient,
        WslInterop wslInterop,
        RememberedDeviceStore rememberedDeviceStore,
        AutoAttachActivityTracker autoAttachActivityTracker,
        ServiceClientConnectionTracker connectionTracker
    )
    {
        this.logger = logger;
        this.usbIpdClient = usbIpdClient;
        this.wslInterop = wslInterop;
        this.rememberedDeviceStore = rememberedDeviceStore;
        this.autoAttachActivityTracker = autoAttachActivityTracker;
        this.connectionTracker = connectionTracker;
        _confirmationPoller = new AttachConfirmationPoller(usbIpdClient, logger);
    }

    public override async Task<GetDevicesResponse> GetDevices(
        GetDevicesRequest request,
        ServerCallContext context
    )
    {
        IReadOnlyList<UsbIpdStateDevice> rawDevices;
        try
        {
            rawDevices = await usbIpdClient.GetDevicesAsync(context.CancellationToken);
        }
        catch (UsbIpdException ex)
        {
            logger.LogWarning("GetDevices failed: {Message}", ex.Message);
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }

        var remembered = rememberedDeviceStore.Load();
        var response = new GetDevicesResponse();

        foreach (var raw in rawDevices)
        {
            var state = UsbIpdStateParser.Classify(raw);
            var instanceId = raw.InstanceId ?? "";
            var isRemembered =
                !string.IsNullOrEmpty(instanceId) && remembered.ContainsKey(instanceId);

            response.Devices.Add(
                new Device
                {
                    InstanceId = instanceId,
                    BusId = raw.BusId ?? "",
                    Description = raw.Description ?? "",
                    HardwareId =
                        UsbIpdStateParser.ExtractVidPid(raw.InstanceId)
                        ?? DeviceMapper.BuildHardwareId(raw)
                        ?? "",
                    State = state.ToString().ToLowerInvariant(),
                    Remembered = isRemembered,
                    PreferredDistro = isRemembered ? remembered[instanceId] : "",
                    Attaching = autoAttachActivityTracker.IsAttaching(instanceId),
                }
            );
        }

        return response;
    }

    public override async Task StreamDevices(
        StreamDevicesRequest request,
        IServerStreamWriter<DeviceEvent> responseStream,
        ServerCallContext context
    )
    {
        connectionTracker.OnStreamConnected();
        var ct = context.CancellationToken;
        var pollInterval = TimeSpan.FromMilliseconds(800);
        var debounceWindow = TimeSpan.FromMilliseconds(1200);

        // Send initial snapshot.
        GetDevicesResponse snapshot;
        try
        {
            snapshot = await GetDevices(new GetDevicesRequest(), context);
        }
        catch
        {
            snapshot = new GetDevicesResponse();
        }

        foreach (var device in snapshot.Devices)
        {
            await responseStream.WriteAsync(
                new DeviceEvent
                {
                    EventType = "snapshot",
                    Device = device,
                    StreamKey = DeviceStreamEventPlanner.BuildBaseKey(device),
                },
                ct
            );
        }

        var seen = DeviceStreamEventPlanner.BuildSnapshot(snapshot.Devices);
        var pending = new Dictionary<string, DeviceStreamEventPlanner.DeviceDelta>(StringComparer.Ordinal);
        DateTimeOffset? pendingSince = null;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(pollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                GetDevicesResponse current;
                try
                {
                    current = await GetDevices(new GetDevicesRequest(), context);
                }
                catch
                {
                    continue;
                }

                var plan = DeviceStreamEventPlanner.Plan(seen, current.Devices);
                foreach (var delta in plan.Deltas)
                {
                    if (pending.TryGetValue(delta.Key, out var existing))
                    {
                        pending[delta.Key] = DeviceStreamEventPlanner.Merge(existing, delta);
                    }
                    else
                    {
                        pending[delta.Key] = delta;
                    }
                }

                var now = DateTimeOffset.UtcNow;
                if (pending.Count > 0)
                {
                    pendingSince ??= now;
                }

                var shouldFlush = pending.Count >= 25
                    || (pendingSince.HasValue && now - pendingSince.Value >= debounceWindow);

                if (shouldFlush)
                {
                    foreach (var delta in pending.Values)
                    {
                        await responseStream.WriteAsync(
                            new DeviceEvent
                            {
                                EventType = delta.EventType,
                                Device = delta.Device,
                                StreamKey = delta.Key,
                            },
                            ct
                        );
                    }

                    pending.Clear();
                    pendingSince = null;
                }

                seen = plan.Snapshot.ToDictionary(
                    pair => pair.Key,
                    pair => DeviceStreamEventPlanner.Clone(pair.Value),
                    StringComparer.Ordinal
                );
            }

            foreach (var delta in pending.Values)
            {
                await responseStream.WriteAsync(
                    new DeviceEvent
                    {
                        EventType = delta.EventType,
                        Device = delta.Device,
                        StreamKey = delta.Key,
                    },
                    ct
                );
            }
        }
        finally
        {
            connectionTracker.OnStreamDisconnected();
        }
    }

    public override async Task<AttachDeviceResponse> AttachDevice(
        AttachDeviceRequest request,
        ServerCallContext context
    )
    {
        var busId = request.BusId?.Trim() ?? string.Empty;
        var wslDistro = request.WslDistro?.Trim() ?? string.Empty;

        if (string.IsNullOrEmpty(busId))
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "BusId is required.")
            );
        if (string.IsNullOrEmpty(wslDistro))
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "WslDistro is required.")
            );

        logger.LogInformation(
            "Attach requested bus={BusId} distro={Distro} remember={Remember}",
            busId,
            wslDistro,
            request.Remember
        );

        IReadOnlyList<UsbIpdStateDevice> devices;
        try
        {
            devices = await usbIpdClient.GetDevicesAsync(context.CancellationToken);
        }
        catch (UsbIpdException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }

        var dev = devices.FirstOrDefault(
            d => string.Equals(d.BusId, busId, StringComparison.OrdinalIgnoreCase)
        );
        if (dev is null)
            return new AttachDeviceResponse
            {
                Ok = false,
                Message = $"No device found with BusId '{busId}'.",
            };

        var requestedInstanceId = request.InstanceId?.Trim() ?? string.Empty;
        var instanceId = requestedInstanceId.Length > 0 ? requestedInstanceId : dev.InstanceId ?? string.Empty;

        var state = UsbIpdStateParser.Classify(dev);

        if (state == DeviceState.Attached)
            return new AttachDeviceResponse { Ok = false, Message = "Device is already attached." };

        ProcessResult distroResult;
        try
        {
            distroResult = await wslInterop.ListDistrosVerboseAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to query WSL distros before attach.");
            return new AttachDeviceResponse
            {
                Ok = false,
                Message = "Failed to query WSL distros.",
            };
        }

        if (distroResult.ExitCode != 0)
        {
            logger.LogWarning("wsl --list --verbose failed during attach: {Err}", distroResult.StdErr);
            return new AttachDeviceResponse
            {
                Ok = false,
                Message = "Cannot validate target WSL distro.",
            };
        }

        var distroExists = DeviceMapper.ParseDistroNames(distroResult.StdOut)
            .Any(d => string.Equals(d, wslDistro, StringComparison.OrdinalIgnoreCase));
        if (!distroExists)
        {
            return new AttachDeviceResponse
            {
                Ok = false,
                Message = $"WSL distro '{wslDistro}' was not found.",
            };
        }

        autoAttachActivityTracker.MarkAttaching(instanceId);
        try
        {
            // Bind first if the device is not yet shared.
            if (state == DeviceState.Available)
            {
                var (bindOk, bindMsg) = await usbIpdClient.BindAsync(
                    busId,
                    context.CancellationToken
                );
                if (!bindOk)
                    return new AttachDeviceResponse { Ok = false, Message = $"Bind failed: {bindMsg}" };
            }

            var (attachOk, attachMsg) = await usbIpdClient.AttachAsync(
                wslDistro,
                busId,
                context.CancellationToken
            );

            if (!attachOk)
                return new AttachDeviceResponse
                {
                    Ok = false,
                    Message = attachMsg.Length > 0 ? attachMsg : "Attach failed.",
                };

            if (request.Remember && !string.IsNullOrEmpty(instanceId))
            {
                rememberedDeviceStore.AddOrUpdate(instanceId, wslDistro);
                logger.LogInformation(
                    "Remembered device {InstanceId} → {Distro}",
                    instanceId,
                    wslDistro
                );
            }

            var (confirmed, confirmationMessage) = await _confirmationPoller.WaitForAttachedStateAsync(
                busId,
                TimeSpan.FromSeconds(8),
                context.CancellationToken
            );

            if (!confirmed)
            {
                return new AttachDeviceResponse
                {
                    Ok = false,
                    Message = confirmationMessage,
                };
            }

            return new AttachDeviceResponse
            {
                Ok = true,
                Message = attachMsg.Length > 0 ? attachMsg : "Device attached.",
            };
        }
        finally
        {
            autoAttachActivityTracker.ClearAttaching(instanceId);
        }
    }

    public override async Task<DetachDeviceResponse> DetachDevice(
        DetachDeviceRequest request,
        ServerCallContext context
    )
    {
        var busId = request.BusId?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(busId))
            throw new RpcException(
                new Status(StatusCode.InvalidArgument, "BusId is required.")
            );

        IReadOnlyList<UsbIpdStateDevice> devices;
        try
        {
            devices = await usbIpdClient.GetDevicesAsync(context.CancellationToken);
        }
        catch (UsbIpdException ex)
        {
            throw new RpcException(new Status(StatusCode.Unavailable, ex.Message));
        }

        var dev = devices.FirstOrDefault(
            d => string.Equals(d.BusId, busId, StringComparison.OrdinalIgnoreCase)
        );
        if (dev is null)
            return new DetachDeviceResponse
            {
                Ok = false,
                Message = $"No device found with BusId '{busId}'.",
            };

        var state = UsbIpdStateParser.Classify(dev);
        if (state != DeviceState.Attached)
        {
            return new DetachDeviceResponse
            {
                Ok = false,
                Message = "Device is not currently attached.",
            };
        }

        var requestedInstanceId = request.InstanceId?.Trim() ?? string.Empty;
        var instanceId = requestedInstanceId.Length > 0 ? requestedInstanceId : dev.InstanceId ?? string.Empty;

        logger.LogInformation(
            "Detach requested bus={BusId} instance={InstanceId}",
            busId,
            instanceId
        );

        autoAttachActivityTracker.MarkAttaching(instanceId);
        try
        {
            var (ok, msg) = await usbIpdClient.DetachAsync(
                busId,
                context.CancellationToken
            );
            if (!ok)
                return new DetachDeviceResponse
                {
                    Ok = false,
                    Message = msg.Length > 0 ? msg : "Detach failed.",
                };

            var (confirmed, confirmationMessage) = await _confirmationPoller.WaitForDetachedStateAsync(
                busId,
                TimeSpan.FromSeconds(8),
                context.CancellationToken
            );

            if (!confirmed)
            {
                return new DetachDeviceResponse
                {
                    Ok = false,
                    Message = confirmationMessage,
                };
            }

            return new DetachDeviceResponse
            {
                Ok = true,
                Message = msg.Length > 0 ? msg : "Device detached.",
            };
        }
        finally
        {
            autoAttachActivityTracker.ClearAttaching(instanceId);
        }
    }

    public override async Task<QueryWslDistrosResponse> QueryWslDistros(
        QueryWslDistrosRequest request,
        ServerCallContext context
    )
    {
        var response = new QueryWslDistrosResponse();

        IReadOnlyList<string> distros;
        try
        {
            distros = await wslInterop.QuerySelectableDistrosAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QueryWslDistros failed.");
            return response;
        }

        foreach (var distro in distros)
            response.Distros.Add(distro);

        return response;
    }
}
