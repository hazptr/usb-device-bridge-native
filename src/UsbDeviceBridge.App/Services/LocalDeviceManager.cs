using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using UsbDeviceBridge.App.Models;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Non-elevated device discovery and state management running in user context.
/// Replaces service-side device RPC endpoints (BUG-0006 fix).
/// </summary>
public sealed class LocalDeviceManager
{
    private readonly UsbIpdClient _usbIpdClient;
    private readonly ILogger<LocalDeviceManager> _logger;

    public LocalDeviceManager(UsbIpdClient usbIpdClient, ILogger<LocalDeviceManager>? logger = null)
    {
        _usbIpdClient = usbIpdClient;
        _logger = logger ?? NullLogger<LocalDeviceManager>.Instance;
    }

    public async Task<IReadOnlyList<Device>> GetDevicesAsync(CancellationToken ct)
    {
        try
        {
            var rawDevices = await _usbIpdClient.GetDevicesAsync(ct);
            var devices = new List<Device>();

            foreach (var raw in rawDevices)
            {
                var state = AppUsbIpdStateParser.Classify(raw);
                devices.Add(new Device
                {
                    InstanceId = raw.InstanceId ?? "",
                    BusId = raw.BusId ?? "",
                    Description = raw.Description ?? "",
                    HardwareId = AppUsbIpdStateParser.ExtractVidPid(raw.InstanceId) ?? "",
                    State = state.ToString().ToLowerInvariant(),
                    Remembered = false,
                    PreferredDistro = "",
                    Attaching = false,
                });
            }

            return devices;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GetDevices failed");
            throw;
        }
    }

    /// <summary>
    /// Attaches device to WSL distro in user context. Caller must bind first if device is Available.
    /// Returns the raw usbipd output so the caller can detect firewall blocks.
    /// </summary>
    public async Task<(bool Ok, string Message)> AttachAsync(
        string busId,
        string wslDistro,
        CancellationToken ct)
    {
        try
        {
            return await _usbIpdClient.AttachAsync(wslDistro, busId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attach failed for {BusId}", busId);
            throw;
        }
    }

    public async Task<(bool Ok, string Message)> DetachAsync(string busId, CancellationToken ct)
    {
        try
        {
            return await _usbIpdClient.DetachAsync(busId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Detach failed for {BusId}", busId);
            throw;
        }
    }
}
