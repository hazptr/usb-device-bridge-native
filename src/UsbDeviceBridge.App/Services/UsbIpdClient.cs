using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using UsbDeviceBridge.App.Interop.UsbIpProtocol;
using UsbDeviceBridge.App.Models;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

public sealed class UsbIpdClient
{
    private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CapabilityProbeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SshAttachTimeout = TimeSpan.FromSeconds(20);

    private readonly string _usbIpdPath;
    private readonly string _tcpHost;
    private readonly int _tcpPort;
    private bool? _supportsRemoteAttach;

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
        => await AttachAsync(
            new AttachTarget { Type = AttachTargetType.Wsl, Name = distro ?? string.Empty },
            busId,
            ct);

    public async Task<(bool Ok, string Message)> AttachAsync(
        AttachTarget target,
        string busId,
        CancellationToken ct)
    {
        var normalizedBusId = (busId ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedBusId))
            return (false, "Bus ID is required.");

        // Bind the device first if not already bound
        var bindResult = await BindAsync(normalizedBusId, ct);
        if (!bindResult.Ok)
            return bindResult;

        (int Code, string? Stdout, string? Stderr) result;
        var normalizedType = NormalizeAttachTargetType(target.Type);
        var targetName = (target.Name ?? string.Empty).Trim();

        try
        {
            string[] args;
            if (normalizedType == AttachTargetType.Ssh)
            {
                if (string.IsNullOrWhiteSpace(targetName))
                    return (false, "SSH target name is required.");

                if (!await SupportsRemoteAttachAsync(ct))
                {
                    return (false,
                        "The installed usbipd-win does not support SSH attach targets (missing '--remote'). "
                        + "This version only supports '--wsl'. Choose a WSL target or upgrade usbipd-win.");
                }

                args = ["attach", "--busid", normalizedBusId, "--remote", targetName];
            }
            else
            {
                args = ["attach", "--busid", normalizedBusId, "--wsl"];
            }

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
            normalizedType == AttachTargetType.Ssh
            && (
                message.Contains("Unrecognized command or argument '--remote'", StringComparison.OrdinalIgnoreCase)
                || message.Contains("Option '--wsl' is required", StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            _supportsRemoteAttach = false;
            return (false,
                "The installed usbipd-win does not support SSH attach targets (missing '--remote'). "
                + "This version only supports '--wsl'. Choose a WSL target or upgrade usbipd-win.");
        }


        return (false, message);
    }

    public async Task<(bool Ok, string Message)> AttachViaSshClientAsync(
        string sshTarget,
        string busId,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sshTarget))
            return (false, "SSH target name is required.");

        if (string.IsNullOrWhiteSpace(busId))
            return (false, "Bus ID is required.");

        var normalizedBusId = busId.Trim();

        // usbip attach always requires root on Linux. Use sudo -n directly.
        // The Setup step writes /etc/sudoers.d/usbip-attach granting passwordless sudo for usbip.
        var attachCommand = $"sudo -n usbip attach --remote 127.0.0.1 --busid {normalizedBusId}";
        var result = await RunSshCommandAsync(sshTarget, attachCommand, ct);

        if (result.Ok)
            return result;

        var needsPassword = result.Message.Contains("password is required", StringComparison.OrdinalIgnoreCase)
            || result.Message.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase)
            || result.Message.Contains("no tty present", StringComparison.OrdinalIgnoreCase)
            || result.Message.Contains("no password was provided", StringComparison.OrdinalIgnoreCase);

        return needsPassword
            ? (false,
                $"SSH client '{sshTarget}' requires passwordless sudo for usbip. "
                + "Run Setup for this client in Settings to configure it.")
            : result;
    }

    private static AttachTargetType NormalizeAttachTargetType(AttachTargetType type)
        => type == AttachTargetType.Ssh
            ? AttachTargetType.Ssh
            : AttachTargetType.Wsl;

    private async Task<bool> SupportsRemoteAttachAsync(CancellationToken ct)
    {
        if (_supportsRemoteAttach.HasValue)
            return _supportsRemoteAttach.Value;

        try
        {
            var (_, stdout, stderr) = await RunCliAsync(["attach", "--help"], ct, CapabilityProbeTimeout);
            var help = $"{stdout}\n{stderr}";
            _supportsRemoteAttach = help.Contains("--remote", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            _supportsRemoteAttach = false;
        }

        return _supportsRemoteAttach.Value;
    }

    public async Task<(bool Ok, string Message)> DetachAsync(string busId, CancellationToken ct)
    {
        var (code, _, stderr) = await RunCliAsync(["detach", "-b", busId], ct);
        return code == 0 ? (true, "") : (false, stderr ?? "detach failed");
    }

    public async Task<(bool Ok, string Message)> BindAsync(string busId, CancellationToken ct)
    {
        try
        {
            var (code, stdout, stderr) = await RunCliAsync(["bind", "--busid", busId], ct, AttachTimeout);
            if (code == 0)
                return (true, "");

            var message = $"{stderr}\n{stdout}".Trim();

            // Device may already be bound; that's fine
            if (message.Contains("already bound", StringComparison.OrdinalIgnoreCase)
                || message.Contains("already in use", StringComparison.OrdinalIgnoreCase))
                return (true, "");

            return (false, message);
        }
        catch (AppUsbIpdTimeoutException)
        {
            return (false, $"usbipd bind timed out after {(int)AttachTimeout.TotalSeconds} seconds.");
        }
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

    private async Task<(bool Ok, string Message)> RunSshCommandAsync(
        string sshTarget,
        string remoteCommand,
        CancellationToken ct,
        string? reverseTunnelSpec = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("BatchMode=yes");
        if (!string.IsNullOrWhiteSpace(reverseTunnelSpec))
        {
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("ExitOnForwardFailure=yes");
            psi.ArgumentList.Add("-R");
            psi.ArgumentList.Add(reverseTunnelSpec);
        }
        psi.ArgumentList.Add(sshTarget);
        psi.ArgumentList.Add(remoteCommand);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(SshAttachTimeout);

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var stdout = (await stdoutTask)?.Trim() ?? string.Empty;
            var stderr = (await stderrTask)?.Trim() ?? string.Empty;
            var message = $"{stderr}\n{stdout}".Trim();
            return process.ExitCode == 0
                ? (true, string.Empty)
                : (false, string.IsNullOrWhiteSpace(message) ? "SSH client attach failed." : message);
        }
        catch (OperationCanceledException)
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

            return (false, $"SSH client attach timed out after {(int)SshAttachTimeout.TotalSeconds} seconds.");
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

