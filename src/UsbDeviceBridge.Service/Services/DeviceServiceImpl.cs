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
    private readonly VersionInfoProvider versionInfoProvider;
    private readonly AttachConfirmationPoller _confirmationPoller;
    private readonly AutoAttachNotificationStore _notificationStore;

    public DeviceServiceImpl(
        ILogger<DeviceServiceImpl> logger,
        UsbIpdClient usbIpdClient,
        WslInterop wslInterop,
        RememberedDeviceStore rememberedDeviceStore,
        AutoAttachActivityTracker autoAttachActivityTracker,
        ServiceClientConnectionTracker connectionTracker,
        VersionInfoProvider versionInfoProvider,
        AutoAttachNotificationStore notificationStore
    )
    {
        this.logger = logger;
        this.usbIpdClient = usbIpdClient;
        this.wslInterop = wslInterop;
        this.rememberedDeviceStore = rememberedDeviceStore;
        this.autoAttachActivityTracker = autoAttachActivityTracker;
        this.connectionTracker = connectionTracker;
        this.versionInfoProvider = versionInfoProvider;
        _confirmationPoller = new AttachConfirmationPoller(usbIpdClient, logger);
        _notificationStore = notificationStore;
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

                // Drain any auto-attach notifications and forward them to the client.
                foreach (var notification in _notificationStore.DrainPending())
                {
                    await responseStream.WriteAsync(
                        new DeviceEvent
                        {
                            EventType = "notification",
                            NotificationMessage = notification.Message,
                            NotificationSeverity = notification.Severity,
                            NotificationCode = notification.Code,
                            NotificationInstanceId = notification.InstanceId,
                            NotificationBusId = notification.BusId,
                            NotificationWslDistro = notification.WslDistro,
                        },
                        ct
                    );
                }
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

            var firewallFixApplied = false;

            if (!attachOk)
            {
                // --- Firewall-recovery path ---
                if (FirewallSignatureClassifier.IsFirewallBlock(attachMsg))
                {
                    var policy = NormalizeFirewallPolicy(request.FirewallFixPolicy);
                    logger.LogInformation(
                        "Firewall-like attach failure detected (policy={Policy}) bus={BusId} distro={Distro}: {Message}",
                        policy, busId, wslDistro, attachMsg);

                    if (policy != "always")
                    {
                        // "ask" or "never" – do not apply the fix automatically.
                        var guidanceMsg = policy == "never"
                            ? "Firewall fix is disabled by policy. Enable 'Auto-fix' in Settings to allow automatic recovery."
                            : "Firewall may be blocking the connection. Enable 'Auto-fix' in Settings or approve recovery when prompted.";

                        return new AttachDeviceResponse
                        {
                            Ok = false,
                            Message = guidanceMsg,
                            FailReason = AttachFailReason.PolicyPrevented,
                        };
                    }

                    // policy == "always" → apply fix and retry once.
                    var (fixOk, fixErr) = await WslFirewallFixer.ApplyPublicProfileFixAsync(
                        logger, context.CancellationToken);

                    if (!fixOk)
                    {
                        logger.LogWarning("Firewall fix failed: {Error}", fixErr);
                        return new AttachDeviceResponse
                        {
                            Ok = false,
                            Message = $"Automatic firewall recovery failed: {fixErr}",
                            FailReason = AttachFailReason.FirewallFixFailed,
                        };
                    }

                    logger.LogInformation("Firewall fix applied; retrying attach bus={BusId} distro={Distro}.", busId, wslDistro);
                    var (retryOk, retryMsg) = await usbIpdClient.AttachAsync(
                        wslDistro, busId, context.CancellationToken);

                    if (!retryOk)
                    {
                        logger.LogWarning(
                            "Attach still failing after firewall fix bus={BusId} distro={Distro}: {Message}",
                            busId, wslDistro, retryMsg);
                        return new AttachDeviceResponse
                        {
                            Ok = false,
                            Message = $"Attach still failing after adjusting the Public firewall profile for WSL vEthernet adapters.\n\n{retryMsg}",
                            FailReason = AttachFailReason.StillFailedAfterFix,
                        };
                    }

                    // Retry succeeded.
                    attachMsg = retryMsg;
                    firewallFixApplied = true;
                }
                else
                {
                    return new AttachDeviceResponse
                    {
                        Ok = false,
                        Message = attachMsg.Length > 0 ? attachMsg : "Attach failed.",
                    };
                }
            }

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
                FirewallFixApplied = firewallFixApplied,
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

        IReadOnlyList<SelectableWslDistro> distros;
        try
        {
            distros = await wslInterop.QuerySelectableDistrosWithStateAsync(context.CancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "QueryWslDistros failed.");
            return response;
        }

        foreach (var distro in distros)
        {
            response.Distros.Add(distro.Name);
            response.DistroStatuses.Add(new DistroStatus
            {
                Name = distro.Name,
                IsRunning = distro.IsRunning,
            });
        }

        return response;
    }

    public override async Task<Usbdevicebridge.V1.VersionInfo> GetVersionInfo(
        GetVersionInfoRequest request,
        ServerCallContext context
    )
    {
        try
        {
            var snapshot = await versionInfoProvider.QueryAsync(context.CancellationToken);
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
            logger.LogWarning(ex, "GetVersionInfo failed.");
            return new Usbdevicebridge.V1.VersionInfo
            {
                FrontendVersion = "N/A",
                ServiceVersion = "Unknown",
                WslVersion = "Unknown",
                UsbipdVersion = "Unknown",
            };
        }
    }

    /// <summary>
    /// Normalises the firewall-fix policy string from a request.
    /// Accepts "always" and "never"; anything else (including empty) maps to "ask".
    /// </summary>
    private static string NormalizeFirewallPolicy(string? raw)
    {
        if (string.Equals(raw, "always", StringComparison.OrdinalIgnoreCase)) return "always";
        if (string.Equals(raw, "never",  StringComparison.OrdinalIgnoreCase)) return "never";
        return "ask";
    }
}
