using UsbDeviceBridge.App.Settings;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

public sealed record SettingsResetResult(bool Succeeded, int ForgottenCount, string ErrorMessage)
{
    public static SettingsResetResult Success(int forgottenCount)
        => new(true, forgottenCount, string.Empty);

    public static SettingsResetResult Failed(string errorMessage)
        => new(false, 0, errorMessage);
}

public sealed class SettingsResetService
{
    private readonly AppRememberedDeviceStore _rememberedStore;
    private readonly LocalDeviceManager _deviceManager;
    private readonly BridgeServiceClient _serviceClient;
    private readonly AppSettingsService _settingsService;

    public SettingsResetService(
        AppRememberedDeviceStore rememberedStore,
        LocalDeviceManager deviceManager,
        BridgeServiceClient serviceClient,
        AppSettingsService settingsService)
    {
        _rememberedStore = rememberedStore;
        _deviceManager = deviceManager;
        _serviceClient = serviceClient;
        _settingsService = settingsService;
    }

    public async Task<SettingsResetResult> ResetAsync(CancellationToken ct = default)
    {
        try
        {
            var remembered = _rememberedStore.Load();
            var forgottenCount = 0;

            // Best-effort: release each device before forgetting it.
            IReadOnlyList<Usbdevicebridge.V1.Device>? devices = null;
            try { devices = await _deviceManager.GetDevicesAsync(ct); } catch { }

            foreach (var instanceId in remembered.Keys.ToList())
            {
                try
                {
                    var device = devices?.FirstOrDefault(d =>
                        string.Equals(d.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

                    if (device is not null && device.State == "attached")
                    {
                        await _deviceManager.DetachAsync(device.BusId, ct);
                    }

                    if (device is not null && device.State != "available")
                    {
                        await _serviceClient.Admin.UnbindDeviceAsync(
                            new UnbindDeviceRequest { BusId = device.BusId, HardwareId = device.HardwareId },
                            cancellationToken: ct);
                    }
                }
                catch
                {
                    // Best effort; continue forgetting remaining devices.
                }

                _rememberedStore.Remove(instanceId);
                forgottenCount++;
            }

            _settingsService.Clear();
            return SettingsResetResult.Success(forgottenCount);
        }
        catch (Exception ex)
        {
            return SettingsResetResult.Failed($"Reset failed. {ex.Message}");
        }
    }
}
