using System.Text.Json;
using System.Text.Json.Serialization;
using UsbDeviceBridge.App.Interop.UsbIpProtocol;

namespace UsbDeviceBridge.App.Models;

public record UsbIpdStateDevice
{
    [JsonPropertyName("BusId")]
    public string? BusId { get; init; }

    [JsonPropertyName("ClientIPAddress")]
    public string? ClientIPAddress { get; init; }

    [JsonPropertyName("Description")]
    public string? Description { get; init; }

    [JsonPropertyName("InstanceId")]
    public string? InstanceId { get; init; }

    [JsonPropertyName("PersistedGuid")]
    public string? PersistedGuid { get; init; }

    [JsonPropertyName("StubInstanceId")]
    public string? StubInstanceId { get; init; }

    public string? DeviceId { get; init; }

    public ushort? VendorId { get; init; }

    public ushort? ProductId { get; init; }

    public byte? DeviceClass { get; init; }
}

public enum AppDeviceState { Available, Shared, Attached, Offline }

public static class AppUsbIpdStateParser
{
    public static (IReadOnlyList<UsbIpdStateDevice> Devices, string? Error) Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("Devices", out var devicesElement))
                return ([], "Missing 'Devices' key in usbipd state output.");

            var devices = JsonSerializer.Deserialize<List<UsbIpdStateDevice>>(
                devicesElement.GetRawText()
            ) ?? [];
            return (devices, null);
        }
        catch (JsonException ex)
        {
            return ([], $"Invalid JSON from usbipd state: {ex.Message}");
        }
    }

    public static AppDeviceState Classify(UsbIpdStateDevice dev)
    {
        if (!string.IsNullOrWhiteSpace(dev.ClientIPAddress)) return AppDeviceState.Attached;
        if (!string.IsNullOrWhiteSpace(dev.StubInstanceId)) return AppDeviceState.Shared;
        if (!string.IsNullOrWhiteSpace(dev.BusId)) return AppDeviceState.Available;
        return AppDeviceState.Offline;
    }

    public static string? ExtractVidPid(string? instanceId)
    {
        if (string.IsNullOrEmpty(instanceId)) return null;
        var upper = instanceId.ToUpperInvariant();
        var vidIdx = upper.IndexOf("VID_", StringComparison.Ordinal);
        var pidIdx = upper.IndexOf("PID_", StringComparison.Ordinal);
        if (vidIdx < 0 || pidIdx < 0) return null;
        if (vidIdx + 8 > upper.Length || pidIdx + 8 > upper.Length) return null;
        var vid = upper.Substring(vidIdx + 4, 4);
        var pid = upper.Substring(pidIdx + 4, 4);
        return $"{vid}:{pid}".ToLowerInvariant();
    }
}
