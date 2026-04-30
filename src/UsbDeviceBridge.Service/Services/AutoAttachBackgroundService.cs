using Microsoft.Extensions.Hosting;
using UsbDeviceBridge.Service.Domain;
using UsbDeviceBridge.Service.Interop;

namespace UsbDeviceBridge.Service.Services;

public sealed class AutoAttachBackgroundService(
    ILogger<AutoAttachBackgroundService> logger,
    UsbIpdClient usbIpdClient,
    WslInterop wslInterop,
    RememberedDeviceStore rememberedDeviceStore,
    AutoAttachActivityTracker autoAttachActivityTracker,
    AutoAttachAttemptCancellationRegistry autoAttachAttemptCancellationRegistry,
    ServiceClientConnectionTracker connectionTracker
) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _nextAttemptUtc = new();
    private readonly Dictionary<string, AutoAttachRetryState> _retryStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _availableDistros = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _nextDistroRefreshUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Auto-attach worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOneIterationAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-attach worker iteration failed.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        logger.LogInformation("Auto-attach worker stopped.");
    }

    private async Task RunOneIterationAsync(CancellationToken ct)
    {
        var remembered = rememberedDeviceStore.Load();
        CleanupStaleState(remembered.Keys);

        if (!connectionTracker.HasConnectedClients)
        {
            ClearAttachingForActiveRetries();
            return;
        }

        if (remembered.Count == 0)
        {
            _retryStates.Clear();
            _nextAttemptUtc.Clear();
            return;
        }

        var distrosReady = await TryRefreshAvailableDistrosAsync(ct);
        if (!distrosReady || _availableDistros.Count == 0)
        {
            ClearAttachingForAllRemembered(remembered.Keys);
            return;
        }

        IReadOnlyList<UsbIpdStateDevice> devices;
        try
        {
            devices = await usbIpdClient.GetDevicesAsync(ct);
        }
        catch (UsbIpdException ex)
        {
            logger.LogDebug("Auto-attach skipped: usbipd unavailable: {Message}", ex.Message);
            return;
        }

        var now = DateTimeOffset.UtcNow;

        foreach (var (instanceId, preferredDistro) in remembered)
        {
            if (!_availableDistros.Contains(preferredDistro))
            {
                ClearRetryState(instanceId);
            }
        }

        var targets = RememberedDeviceAutoAttachPlanner.SelectAttachTargets(
            remembered,
            devices,
            _availableDistros,
            _nextAttemptUtc,
            now
        );

        var targetIds = new HashSet<string>(targets.Select(t => t.InstanceId), StringComparer.OrdinalIgnoreCase);
        var retrySnapshot = _retryStates.ToArray();
        foreach (var (instanceId, state) in retrySnapshot)
        {
            if (!state.Abandoned && _nextAttemptUtc.TryGetValue(instanceId, out var nextAttempt) && nextAttempt > now)
            {
                autoAttachActivityTracker.MarkAttaching(instanceId);
            }

            if (state.Abandoned && !targetIds.Contains(instanceId))
            {
                ClearRetryState(instanceId);
            }
        }

        foreach (var target in targets)
        {
            var instanceId = target.InstanceId;
            var busId = target.BusId;
            var distro = target.Distro;
            var state = target.State;

            if (_retryStates.TryGetValue(instanceId, out var existingRetry) && existingRetry.Abandoned)
            {
                continue;
            }

            if (state == DeviceState.Attached)
            {
                ClearRetryState(instanceId);
                continue;
            }

            if (state == DeviceState.Offline)
            {
                ClearRetryState(instanceId);
                continue;
            }

            autoAttachActivityTracker.MarkAttaching(instanceId);
            using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            autoAttachAttemptCancellationRegistry.Register(instanceId, operationCts);
            try
            {
                if (state == DeviceState.Available)
                {
                    var (bindOk, bindMsg) = await usbIpdClient.BindAsync(busId, operationCts.Token);
                    if (!bindOk)
                    {
                        logger.LogDebug("Auto-attach bind failed for {InstanceId}/{BusId}: {Message}", instanceId, busId, bindMsg);
                        RecordFailure(instanceId, now, $"bind failed: {bindMsg}");
                        continue;
                    }
                }

                logger.LogInformation(
                    "Auto-attach attempt device={InstanceId} bus={BusId} distro={Distro}",
                    instanceId,
                    busId,
                    distro
                );

                var (attachOk, attachMsg) = await usbIpdClient.AttachAsync(distro, busId, operationCts.Token);
                if (attachOk)
                {
                    logger.LogInformation("Auto-attached remembered device {InstanceId} ({BusId}) to distro {Distro}.", instanceId, busId, distro);
                    _retryStates[instanceId] = AutoAttachRetryPolicy.RecordSuccess(now);
                    _nextAttemptUtc.Remove(instanceId);
                    autoAttachActivityTracker.ClearAttaching(instanceId);
                }
                else
                {
                    logger.LogDebug("Auto-attach failed for {InstanceId}/{BusId} to {Distro}: {Message}", instanceId, busId, distro, attachMsg);
                    RecordFailure(instanceId, now, attachMsg);
                }
            }
            catch (OperationCanceledException) when (operationCts.IsCancellationRequested)
            {
                logger.LogInformation("Auto-attach attempt canceled for {InstanceId}.", instanceId);
                ClearRetryState(instanceId);
            }
            finally
            {
                autoAttachAttemptCancellationRegistry.Complete(instanceId);
                if (!_nextAttemptUtc.TryGetValue(instanceId, out var nextAttempt) || nextAttempt <= now)
                {
                    autoAttachActivityTracker.ClearAttaching(instanceId);
                }
            }
        }
    }

    private void RecordFailure(string instanceId, DateTimeOffset now, string details)
    {
        _retryStates.TryGetValue(instanceId, out var previous);
        var retry = AutoAttachRetryPolicy.RecordFailure(now, previous);
        _retryStates[instanceId] = retry;

        if (retry.Abandoned)
        {
            _nextAttemptUtc.Remove(instanceId);
            autoAttachActivityTracker.ClearAttaching(instanceId);
            logger.LogWarning(
                "Auto-attach stopped for {InstanceId} after {Attempts} failures. Last error: {Error}",
                instanceId,
                retry.FailureCount,
                details
            );
            return;
        }

        _nextAttemptUtc[instanceId] = retry.NextAttemptUtc;
        logger.LogInformation(
            "Auto-attach retry scheduled for {InstanceId} in {DelaySeconds}s (attempt {Attempt}/{MaxAttempts}). Last error: {Error}",
            instanceId,
            Math.Max(0, (retry.NextAttemptUtc - now).TotalSeconds),
            retry.FailureCount,
            AutoAttachRetryPolicy.MaxAttempts,
            details
        );
    }

    private void CleanupStaleState(IEnumerable<string> rememberedKeys)
    {
        var keySet = new HashSet<string>(rememberedKeys, StringComparer.OrdinalIgnoreCase);

        var staleRetryKeys = _retryStates.Keys.Where(k => !keySet.Contains(k)).ToList();
        foreach (var key in staleRetryKeys)
        {
            _retryStates.Remove(key);
            autoAttachActivityTracker.ClearAttaching(key);
        }

        var staleAttemptKeys = _nextAttemptUtc.Keys.Where(k => !keySet.Contains(k)).ToList();
        foreach (var key in staleAttemptKeys)
        {
            _nextAttemptUtc.Remove(key);
        }
    }

    private void ClearAttachingForAllRemembered(IEnumerable<string> rememberedKeys)
    {
        foreach (var instanceId in rememberedKeys)
        {
            autoAttachActivityTracker.ClearAttaching(instanceId);
        }
    }

    private void ClearAttachingForActiveRetries()
    {
        foreach (var key in _retryStates.Keys)
        {
            autoAttachActivityTracker.ClearAttaching(key);
        }
    }

    private void ClearRetryState(string instanceId)
    {
        _nextAttemptUtc.Remove(instanceId);
        _retryStates.Remove(instanceId);
        autoAttachActivityTracker.ClearAttaching(instanceId);
    }

    private async Task<bool> TryRefreshAvailableDistrosAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (now < _nextDistroRefreshUtc)
            return true;

        ProcessResult distroResult;
        try
        {
            distroResult = await wslInterop.ListDistrosVerboseAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Auto-attach skipped: WSL distro query failed.");
            _nextDistroRefreshUtc = now.AddSeconds(5);
            return false;
        }

        if (distroResult.ExitCode != 0)
        {
            logger.LogDebug("Auto-attach skipped: WSL distro query exit code {Code}", distroResult.ExitCode);
            _nextDistroRefreshUtc = now.AddSeconds(5);
            return false;
        }

        _availableDistros.Clear();
        foreach (var distro in RememberedDeviceAutoAttachPlanner.ParseAvailableDistros(distroResult.StdOut))
            _availableDistros.Add(distro);

        _nextDistroRefreshUtc = now.AddSeconds(10);
        return true;
    }
}
