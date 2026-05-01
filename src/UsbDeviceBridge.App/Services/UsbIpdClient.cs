using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UsbDeviceBridge.App.Interop.UsbIpProtocol;
using UsbDeviceBridge.App.Models;

namespace UsbDeviceBridge.App.Services;

public sealed class UsbIpdClient
{
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);

    private readonly string _usbIpdPath;
    private readonly string _tcpHost;
    private readonly int _tcpPort;

    public UsbIpdClient(
        string? usbIpdPath = null,
        string tcpHost = "127.0.0.1",
        int tcpPort = 3240
    )
    {
        _usbIpdPath = usbIpdPath ?? FindUsbIpd();
        _tcpHost = tcpHost;
        _tcpPort = tcpPort;
    }

    public string UsbIpdPath => _usbIpdPath;

    public async Task<IReadOnlyList<UsbIpdStateDevice>> GetDevicesAsync(CancellationToken ct)
    {
        var tcpDevices = await GetDevicesViaTcpAsync(ct);
        var stateDevices = await GetDevicesFromStateAsync(ct);

        return MergeDevices(tcpDevices, stateDevices);
    }

    public async Task<(bool Ok, string Message)> AttachAsync(
        string distro,
        string busId,
        CancellationToken ct
    )
    {
        (int Code, string? Stdout, string? Stderr) result;
        try
        {
            var args = string.IsNullOrWhiteSpace(distro)
                ? new[] { "attach", "--busid", busId, "--wsl" }
                : new[] { "attach", "--busid", busId, "--wsl", distro };

            result = await RunCliAsync(args, ct, AttachTimeout);
        }
        catch (AppUsbIpdTimeoutException)
        {
            return (false, $"usbipd attach timed out after {(int)AttachTimeout.TotalSeconds} seconds.");
        }

        var (code, stdout, stderr) = result;
        if (code == 0)
            return (true, "");

        var message = $"{stderr}\n{stdout}".Trim();
        if (
            !string.IsNullOrWhiteSpace(distro)
            && (
                message.Contains("--distribution", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unrecognized", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            try
            {
                var (fallbackCode, fallbackStdout, fallbackStderr) = await RunCliAsync(
                    ["attach", "--busid", busId, "--wsl", "--distribution", distro],
                    ct,
                    AttachTimeout
                );
                if (fallbackCode == 0)
                    return (true, "");

                return (false, $"{fallbackStderr}\n{fallbackStdout}".Trim());
            }
            catch (AppUsbIpdTimeoutException)
            {
                return (false, $"usbipd attach timed out after {(int)AttachTimeout.TotalSeconds} seconds.");
            }
        }

        return (false, message);
    }

    public async Task<(bool Ok, string Message)> DetachAsync(string busId, CancellationToken ct)
    {
        var (code, _, stderr) = await RunCliAsync(["detach", "-b", busId], ct);
        return code == 0 ? (true, "") : (false, stderr ?? "detach failed");
    }

    public async Task<TcpClient> ConnectTcpAsync(CancellationToken ct)
    {
        var client = new TcpClient();
        using var reg = ct.Register(() => { try { client.Dispose(); } catch { } });
        await client.ConnectAsync(_tcpHost, _tcpPort, ct);
        return client;
    }

    public async Task<IReadOnlyList<UsbIpExportedDevice>> GetDevicesViaTcpAsync(CancellationToken ct)
    {
        using var client = await ConnectTcpAsync(ct);
        using var stream = client.GetStream();

        var request = UsbIpPacketCodec.BuildDevListRequest();
        await stream.WriteAsync(request, ct);
        await stream.FlushAsync(ct);

        var headerBytes = await ReadExactAsync(stream, UsbIpPacketCodec.CommonHeaderLength, ct);
        if (!UsbIpPacketCodec.TryParseCommonHeader(headerBytes, out var header))
            throw new InvalidOperationException("Invalid USB/IP common header in response.");

        if (header.Code != UsbIpCodes.OpRepDevList)
            throw new InvalidOperationException(
                $"Unexpected USB/IP response code 0x{header.Code:x4}."
            );

        if (!UsbIpPacketCodec.TryGetDeviceCount(header, out var deviceCount))
            throw new InvalidOperationException("Invalid USB/IP device count in response header.");

        var devices = new List<UsbIpExportedDevice>(deviceCount);
        for (var i = 0; i < deviceCount; i++)
        {
            var deviceRecordBytes = await ReadExactAsync(
                stream,
                UsbIpPacketCodec.ExportedDeviceRecordLength,
                ct
            );

            if (!UsbIpPacketCodec.TryParseExportedDeviceRecord(deviceRecordBytes, out var device))
                throw new InvalidOperationException("Invalid USB/IP exported device record.");

            var interfaceBytesToRead = checked(
                device.InterfaceCount * UsbIpPacketCodec.InterfaceRecordLength
            );
            if (interfaceBytesToRead > 0)
            {
                _ = await ReadExactAsync(stream, interfaceBytesToRead, ct);
            }

            devices.Add(device);
        }

        return devices;
    }

    private async Task<(int Code, string? Stdout, string? Stderr)> RunCliAsync(
        string[] args,
        CancellationToken ct,
        TimeSpan? timeout = null
    )
    {
        CancellationTokenSource? timeoutCts = null;
        if (timeout is TimeSpan timeoutValue)
        {
            timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutValue);
        }

        using (timeoutCts)
        {
            var effectiveToken = timeoutCts?.Token ?? ct;

            var psi = new ProcessStartInfo
            {
                FileName = _usbIpdPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.Start();

            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(effectiveToken);
                var stderrTask = process.StandardError.ReadToEndAsync(effectiveToken);
                await process.WaitForExitAsync(effectiveToken);

                return (process.ExitCode, await stdoutTask, await stderrTask);
            }
            catch (OperationCanceledException) when (timeoutCts is not null && !ct.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Best effort kill.
                }

                throw new AppUsbIpdTimeoutException($"usbipd {string.Join(' ', args)} timed out.");
            }
        }
    }

    private async Task<IReadOnlyList<UsbIpdStateDevice>> GetDevicesFromStateAsync(CancellationToken ct)
    {
        var (code, stdout, stderr) = await RunCliAsync(["state"], ct);
        if (code != 0)
            throw new AppUsbIpdException($"usbipd state failed: {stderr ?? stdout ?? "unknown error"}");

        var (devices, error) = AppUsbIpdStateParser.Parse(stdout ?? string.Empty);
        if (error is not null)
            throw new AppUsbIpdException(error);

        return devices;
    }

    private static IReadOnlyList<UsbIpdStateDevice> MergeDevices(
        IReadOnlyList<UsbIpExportedDevice> tcpDevices,
        IReadOnlyList<UsbIpdStateDevice> stateDevices
    )
    {
        var merged = new List<UsbIpdStateDevice>(Math.Max(tcpDevices.Count, stateDevices.Count));
        var stateByBusId = new Dictionary<string, UsbIpdStateDevice>(StringComparer.OrdinalIgnoreCase);

        foreach (var stateDevice in stateDevices)
        {
            if (!string.IsNullOrWhiteSpace(stateDevice.BusId))
                stateByBusId[stateDevice.BusId] = stateDevice;
        }

        var seenBusIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tcpDevice in tcpDevices)
        {
            stateByBusId.TryGetValue(tcpDevice.BusId, out var stateDevice);
            seenBusIds.Add(tcpDevice.BusId);

            merged.Add(
                new UsbIpdStateDevice
                {
                    BusId = tcpDevice.BusId,
                    Description = stateDevice?.Description ?? string.Empty,
                    InstanceId = stateDevice?.InstanceId,
                    ClientIPAddress = stateDevice?.ClientIPAddress,
                    PersistedGuid = stateDevice?.PersistedGuid,
                    StubInstanceId = stateDevice?.StubInstanceId,
                    DeviceId = tcpDevice.DeviceId,
                    VendorId = tcpDevice.VendorId,
                    ProductId = tcpDevice.ProductId,
                    DeviceClass = tcpDevice.DeviceClass,
                }
            );
        }

        foreach (var stateDevice in stateDevices)
        {
            var hasBusId = !string.IsNullOrWhiteSpace(stateDevice.BusId);
            if (!hasBusId || !seenBusIds.Contains(stateDevice.BusId!))
                merged.Add(stateDevice);
        }

        return merged;
    }

    private static async Task<byte[]> ReadExactAsync(
        NetworkStream stream,
        int length,
        CancellationToken ct
    )
    {
        var buffer = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, length - read), ct);
            if (n == 0)
                throw new InvalidOperationException(
                    "Socket closed before reading expected USB/IP bytes."
                );
            read += n;
        }
        return buffer;
    }

    private static string FindUsbIpd()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
        var pfx86 =
            Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            ?? @"C:\Program Files (x86)";
        foreach (var dir in new[] { pf, pfx86 })
        {
            var candidate = Path.Combine(dir, "usbipd-win", "usbipd.exe");
            if (File.Exists(candidate))
                return candidate;
        }
        return "usbipd";
    }
}

public sealed class AppUsbIpdException(string message) : Exception(message);

public sealed class AppUsbIpdTimeoutException(string message) : Exception(message);
