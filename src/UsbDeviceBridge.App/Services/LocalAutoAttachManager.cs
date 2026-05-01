using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UsbDeviceBridge.App.Models;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Manages auto-attach of remembered devices from the app process (user context).
/// Replaces service-side AutoAttachBackgroundService (BUG-0006 fix).
/// Runs only while the app is open; remembered state persists via AppRememberedDeviceStore.
/// </summary>
public sealed class LocalAutoAttachManager : IDisposable
{
    private static readonly TimeSpan AbandonRetryDelay = TimeSpan.FromMinutes(5);

    private readonly BridgeServiceClient _client;
    private readonly LocalDeviceManager _deviceManager;
    private readonly AppRememberedDeviceStore _rememberedStore;
    private readonly Func<ForceRetryRequest, Task<ForceRetryDecision>>? _requestForceRetryAsync;
    private readonly ILogger<LocalAutoAttachManager> _logger;
    private readonly ConcurrentDictionary<string, AttachRetryState> _retryStates = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _nextRetryTime = new();
    private readonly ConcurrentDictionary<string, string> _lastKnownBusId = new();
    private CancellationTokenSource? _runLoopCts;
    private Task? _runLoopTask;

    /// <summary>Raised when the "attaching" spinner state changes for a device.</summary>
    public event Action<string, bool>? AttachingStateChanged;

    /// <summary>Raised when auto-attach repeatedly fails and is paused.</summary>
    public event Action<string, string>? AutoAttachFailed;

    /// <summary>Raised for immediate auto-attach user-visible outcomes.</summary>
    public event Action<string, NotificationSeverity>? AutoAttachNotification;

    public LocalAutoAttachManager(
        BridgeServiceClient client,
        LocalDeviceManager deviceManager,
        AppRememberedDeviceStore rememberedStore,
        Func<ForceRetryRequest, Task<ForceRetryDecision>>? requestForceRetryAsync = null,
        ILogger<LocalAutoAttachManager>? logger = null)
    {
        _client = client;
        _deviceManager = deviceManager;
        _rememberedStore = rememberedStore;
        _requestForceRetryAsync = requestForceRetryAsync;
        _logger = logger ?? NullLogger<LocalAutoAttachManager>.Instance;
    }

    public void Start()
    {
        if (_runLoopTask is not null && !_runLoopTask.IsCompleted)
            return;

        _runLoopCts = new CancellationTokenSource();
        _runLoopTask = Task.Run(() => RunLoopAsync(_runLoopCts.Token));
    }

    public void Stop()
    {
        _runLoopCts?.Cancel();
        try { _runLoopTask?.Wait(TimeSpan.FromSeconds(5)); } catch { }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        const int pollIntervalMs = 2000;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollIntervalMs, ct);
                await RunOneIterationAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-attach iteration failed");
            }
        }
    }

    private async Task RunOneIterationAsync(CancellationToken ct)
    {
        var remembered = _rememberedStore.Load();
        if (remembered.Count == 0)
        {
            _retryStates.Clear();
            _nextRetryTime.Clear();
            _lastKnownBusId.Clear();
            return;
        }

        IReadOnlyList<Device> devices;
        try
        {
            devices = await _deviceManager.GetDevicesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Device query failed; skipping auto-attach iteration");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var devicesByInstanceId = devices.ToDictionary(d => d.InstanceId, StringComparer.OrdinalIgnoreCase);

        CleanUpDisappearedNonRemembered(devicesByInstanceId, remembered);

        foreach (var (instanceId, distro) in remembered)
        {
            if (!devicesByInstanceId.TryGetValue(instanceId, out var device))
                continue;

            if (!string.IsNullOrEmpty(device.BusId))
                _lastKnownBusId[instanceId] = device.BusId;

            var state = ParseDeviceState(device.State);
            if (state == AppDeviceState.Attached)
            {
                _retryStates.TryRemove(instanceId, out _);
                _nextRetryTime.TryRemove(instanceId, out _);
                AttachingStateChanged?.Invoke(instanceId, false);
                continue;
            }

            if (state == AppDeviceState.Offline)
            {
                _retryStates.TryRemove(instanceId, out _);
                _nextRetryTime.TryRemove(instanceId, out _);
                continue;
            }

            if (_nextRetryTime.TryGetValue(instanceId, out var nextTime) && nextTime > now)
                continue;

            await AttemptAttachAsync(instanceId, device.BusId, device.HardwareId, distro, state, now, ct);
        }
    }

    private void CleanUpDisappearedNonRemembered(
        Dictionary<string, Device> currentDevices,
        Dictionary<string, string> remembered)
    {
        var rememberedSet = new HashSet<string>(remembered.Keys, StringComparer.OrdinalIgnoreCase);
        var currentInstanceIds = new HashSet<string>(currentDevices.Keys, StringComparer.OrdinalIgnoreCase);

        foreach (var (instanceId, _) in _lastKnownBusId.ToArray())
        {
            if (rememberedSet.Contains(instanceId))
                continue;

            if (currentInstanceIds.Contains(instanceId))
                continue;

            _lastKnownBusId.TryRemove(instanceId, out _);
            _logger.LogInformation("Non-remembered device {InstanceId} disappeared", instanceId);
        }
    }

    private async Task AttemptAttachAsync(
        string instanceId,
        string busId,
        string hardwareId,
        string distro,
        AppDeviceState state,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(busId))
        {
            RecordFailure(instanceId, now, "Missing bus ID for auto-attach.");
            return;
        }

        _logger.LogInformation("Auto-attach attempt {InstanceId} to {Distro}", instanceId, distro);
        AttachingStateChanged?.Invoke(instanceId, true);

        var didBind = false;
        try
        {
            if (state == AppDeviceState.Available)
            {
                var bindResp = await _client.Admin.BindDeviceAsync(
                    new BindDeviceRequest { BusId = busId, Force = false },
                    cancellationToken: ct);

                if (!bindResp.Ok)
                {
                    if (BusySignatureClassifier.IsBusyWithForceAvailable(bindResp.Message))
                    {
                        var forceDecision = await RequestForceRetryDecisionAsync(
                            instanceId,
                            busId,
                            distro,
                            ForceRetryStage.Bind);

                        if (!forceDecision.RetryWithForce)
                        {
                            var cancelled = AttachToastMessages.ForceRetryCancelledAutoAttach(instanceId, ForceRetryStage.Bind);
                            AutoAttachNotification?.Invoke(cancelled, NotificationSeverity.Warning);
                            RecordFailure(instanceId, now, cancelled);
                            return;
                        }

                        var forceBindResp = await _client.Admin.BindDeviceAsync(
                            new BindDeviceRequest { BusId = busId, Force = true },
                            cancellationToken: ct);

                        if (!forceBindResp.Ok)
                        {
                            var forceBindMessage = AttachToastMessages.ForceRetryFailedAutoAttach(
                                instanceId,
                                ForceRetryStage.Bind,
                                forceBindResp.Message);
                            AutoAttachNotification?.Invoke(forceBindMessage, NotificationSeverity.Error);
                            RecordFailure(instanceId, now, forceBindResp.Message);
                            return;
                        }

                        didBind = true;
                        AutoAttachNotification?.Invoke(
                            AttachToastMessages.ForceRetrySucceededAutoAttach(instanceId, ForceRetryStage.Bind),
                            NotificationSeverity.Info);
                    }
                    else
                    {
                        _logger.LogDebug("Auto-bind failed for {InstanceId}: {Message}", instanceId, bindResp.Message);
                        RecordFailure(instanceId, now, bindResp.Message);
                        return;
                    }
                }
                else
                {
                    didBind = true;
                }
            }

            var (ok, msg) = await _deviceManager.AttachAsync(busId, distro, ct);
            if (ok)
            {
                _logger.LogInformation("Auto-attached {InstanceId}", instanceId);
                _retryStates[instanceId] = new AttachRetryState { LastSuccessUtc = now };
                _nextRetryTime.TryRemove(instanceId, out _);
                AttachingStateChanged?.Invoke(instanceId, false);
            }
            else
            {
                if (BusySignatureClassifier.IsBusyWithForceAvailable(msg))
                {
                    var forceDecision = await RequestForceRetryDecisionAsync(
                        instanceId,
                        busId,
                        distro,
                        ForceRetryStage.Bind);

                    if (!forceDecision.RetryWithForce)
                    {
                        var cancelled = AttachToastMessages.ForceRetryCancelledAutoAttach(instanceId, ForceRetryStage.Bind);
                        AutoAttachNotification?.Invoke(cancelled, NotificationSeverity.Warning);
                        await TryUnbindAsync(busId, hardwareId);
                        RecordFailure(instanceId, now, cancelled);
                        return;
                    }

                    var forceBindResp = await _client.Admin.BindDeviceAsync(
                        new BindDeviceRequest { BusId = busId, Force = true },
                        cancellationToken: ct);

                    if (!forceBindResp.Ok)
                    {
                        var forceBindMessage = AttachToastMessages.ForceRetryFailedAutoAttach(
                            instanceId,
                            ForceRetryStage.Bind,
                            forceBindResp.Message);
                        AutoAttachNotification?.Invoke(forceBindMessage, NotificationSeverity.Error);
                        await TryUnbindAsync(busId, hardwareId);
                        RecordFailure(instanceId, now, forceBindResp.Message);
                        return;
                    }

                    var (retryAttachOk, retryAttachMsg) = await _deviceManager.AttachAsync(busId, distro, ct);
                    if (!retryAttachOk)
                    {
                        _logger.LogDebug("Auto-attach retry after force-bind failed for {InstanceId}: {Message}", instanceId, retryAttachMsg);
                        await TryUnbindAsync(busId, hardwareId);
                        RecordFailure(instanceId, now, retryAttachMsg);
                        return;
                    }

                    _logger.LogInformation("Auto-attach succeeded after force-bind for {InstanceId}", instanceId);
                    _retryStates[instanceId] = new AttachRetryState { LastSuccessUtc = now };
                    _nextRetryTime.TryRemove(instanceId, out _);
                    AutoAttachNotification?.Invoke(
                        AttachToastMessages.ForceRetrySucceededAutoAttach(instanceId, ForceRetryStage.Bind),
                        NotificationSeverity.Info);
                    return;
                }

                // Attach failed after bind — unbind so device doesn't stay "shared".
                if (didBind)
                    await TryUnbindAsync(busId, hardwareId);

                _logger.LogDebug("Auto-attach failed for {InstanceId}: {Message}", instanceId, msg);
                RecordFailure(instanceId, now, msg);
            }
        }
        catch (Exception ex)
        {
            if (didBind)
                await TryUnbindAsync(busId, hardwareId);
            _logger.LogDebug(ex, "Auto-attach exception for {InstanceId}", instanceId);
            RecordFailure(instanceId, now, ex.Message);
        }
        finally
        {
            AttachingStateChanged?.Invoke(instanceId, false);
        }
    }

    private void RecordFailure(string instanceId, DateTimeOffset now, string message)
    {
        _retryStates.TryGetValue(instanceId, out var current);
        var count = (current?.FailureCount ?? 0) + 1;
        const int maxAttempts = 5;
        var normalizedMessage = string.IsNullOrWhiteSpace(message) ? "Attach failed." : message.Trim();

        if (count >= maxAttempts)
        {
            _logger.LogWarning("Auto-attach abandoned for {InstanceId} after {Count} failures", instanceId, count);
            _retryStates[instanceId] = new AttachRetryState { FailureCount = maxAttempts };
            _nextRetryTime[instanceId] = now.Add(AbandonRetryDelay);

            AutoAttachFailed?.Invoke(
                instanceId,
                $"Auto-attach failed for {instanceId}: {normalizedMessage}");

            _logger.LogInformation(
                "Auto-attach retry delayed for {InstanceId} until {RetryTime} after repeated failures",
                instanceId,
                _nextRetryTime[instanceId]);

            return;
        }

        var backoffSeconds = count switch
        {
            1 => 2,
            2 => 5,
            3 => 15,
            _ => 60,
        };

        _retryStates[instanceId] = new AttachRetryState { FailureCount = count };
        _nextRetryTime[instanceId] = now.AddSeconds(backoffSeconds);

        _logger.LogInformation(
            "Auto-attach retry scheduled for {InstanceId} in {Seconds}s (attempt {Count}/{Max})",
            instanceId, backoffSeconds, count, maxAttempts);
    }

    private static AppDeviceState ParseDeviceState(string stateStr)
    {
        return stateStr?.ToLowerInvariant() switch
        {
            "attached" => AppDeviceState.Attached,
            "shared" => AppDeviceState.Shared,
            "offline" => AppDeviceState.Offline,
            _ => AppDeviceState.Available,
        };
    }

    private async Task<ForceRetryDecision> RequestForceRetryDecisionAsync(
        string instanceId,
        string busId,
        string distro,
        ForceRetryStage stage)
    {
        if (_requestForceRetryAsync is null)
            return new ForceRetryDecision(RetryWithForce: false);

        var decision = await _requestForceRetryAsync(new ForceRetryRequest(
            DeviceDescription: instanceId,
            InstanceId: instanceId,
            BusId: busId,
            WslDistro: distro,
            Stage: stage,
            IsAutoAttach: true));

        return decision;
    }

    private async Task TryUnbindAsync(string busId, string hardwareId = "")
    {
        try
        {
            await _client.Admin.UnbindDeviceAsync(
                new UnbindDeviceRequest { BusId = busId, HardwareId = hardwareId },
                cancellationToken: CancellationToken.None);
        }
        catch
        {
            // Best-effort unbind; service may be unavailable.
        }
    }

    public void Dispose()
    {
        Stop();
        _runLoopCts?.Dispose();
    }

    private sealed class AttachRetryState
    {
        public int FailureCount { get; set; }
        public DateTimeOffset LastSuccessUtc { get; set; }
    }
}
