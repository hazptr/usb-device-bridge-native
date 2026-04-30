using UsbDeviceBridge.App.Settings;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

public sealed record ForgetDeviceOutcome(bool Ok, string Message);

public interface IRememberedDeviceResetClient
{
    Task<IReadOnlyList<string>> GetRememberedInstanceIdsAsync(CancellationToken cancellationToken = default);
    Task<ForgetDeviceOutcome> ForgetDeviceAsync(string instanceId, CancellationToken cancellationToken = default);
}

public sealed class BridgeRememberedDeviceResetClient : IRememberedDeviceResetClient
{
    private readonly AutoAttachService.AutoAttachServiceClient _autoAttachClient;

    public BridgeRememberedDeviceResetClient(AutoAttachService.AutoAttachServiceClient autoAttachClient)
    {
        _autoAttachClient = autoAttachClient;
    }

    public async Task<IReadOnlyList<string>> GetRememberedInstanceIdsAsync(CancellationToken cancellationToken = default)
    {
        var response = await _autoAttachClient.GetRememberedDevicesAsync(
            new GetRememberedDevicesRequest(),
            cancellationToken: cancellationToken);

        return response.Devices
            .Select(d => d.InstanceId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public async Task<ForgetDeviceOutcome> ForgetDeviceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var response = await _autoAttachClient.ForgetDeviceAsync(
            new ForgetDeviceRequest { InstanceId = instanceId },
            cancellationToken: cancellationToken);

        return new ForgetDeviceOutcome(response.Ok, response.Message);
    }
}

public sealed record SettingsResetResult(bool Succeeded, int ForgottenCount, string ErrorMessage)
{
    public static SettingsResetResult Success(int forgottenCount)
        => new(true, forgottenCount, string.Empty);

    public static SettingsResetResult Failed(string errorMessage)
        => new(false, 0, errorMessage);
}

public sealed class SettingsResetService
{
    private readonly IRememberedDeviceResetClient _rememberedDeviceClient;
    private readonly AppSettingsService _settingsService;

    public SettingsResetService(IRememberedDeviceResetClient rememberedDeviceClient, AppSettingsService settingsService)
    {
        _rememberedDeviceClient = rememberedDeviceClient;
        _settingsService = settingsService;
    }

    public async Task<SettingsResetResult> ResetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var instanceIds = await _rememberedDeviceClient.GetRememberedInstanceIdsAsync(cancellationToken);
            var forgottenCount = 0;

            foreach (var instanceId in instanceIds)
            {
                var forgetResult = await _rememberedDeviceClient.ForgetDeviceAsync(instanceId, cancellationToken);
                if (!forgetResult.Ok)
                {
                    return SettingsResetResult.Failed(
                        $"Failed to forget device '{instanceId}'. {forgetResult.Message}");
                }

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
