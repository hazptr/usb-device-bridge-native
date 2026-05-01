using Grpc.Core;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Developer test harness for the service and app-side operations.
/// Invoked when the app is launched with arguments (e.g. from dev.ps1).
/// </summary>
public static class CliRunner
{
    private const string DefaultAddress = "http://127.0.0.1:5205";

    public static async Task RunAsync(string[] args)
    {
        var argList = args.ToList();
        var serviceAddress = DefaultAddress;

        var urlFlag = argList.IndexOf("--url");
        if (urlFlag >= 0 && urlFlag + 1 < argList.Count)
        {
            serviceAddress = argList[urlFlag + 1];
            argList.RemoveRange(urlFlag, 2);
        }

        serviceAddress =
            Environment.GetEnvironmentVariable("USB_DEVICE_BRIDGE_SERVICE_URL")
            ?? serviceAddress;

        var command = argList.ElementAtOrDefault(0)?.ToLowerInvariant() ?? "help";
        var cmdArgs = argList.Skip(1).ToArray();

        using var client = new BridgeServiceClient(serviceAddress);
        var usbIpdClient = new UsbIpdClient();
        var deviceManager = new LocalDeviceManager(usbIpdClient);
        var rememberedStore = new AppRememberedDeviceStore();

        try
        {
            switch (command)
            {
                case "devices" or "d":
                    await DevicesCommand(deviceManager, rememberedStore);
                    break;
                case "distros":
                    await DistrosCommand();
                    break;
                case "attach" or "a":
                    await AttachCommand(client, deviceManager, cmdArgs);
                    break;
                case "detach" or "x":
                    await DetachCommand(client, deviceManager, cmdArgs);
                    break;
                case "remember" or "r":
                    RememberCommand(rememberedStore, cmdArgs);
                    break;
                case "forget" or "f":
                    ForgetCommand(rememberedStore, cmdArgs);
                    break;
                case "remembered" or "rm":
                    RememberedCommand(rememberedStore);
                    break;
                default:
                    PrintHelp(serviceAddress);
                    break;
            }
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
        {
            Console.Error.WriteLine($"Service unavailable at {serviceAddress}");
            Console.Error.WriteLine("Start it with: ./scripts/dev.ps1 service");
            Environment.Exit(1);
        }
    }

    private static async Task DevicesCommand(
        LocalDeviceManager deviceManager,
        AppRememberedDeviceStore rememberedStore)
    {
        var devices = await deviceManager.GetDevicesAsync(CancellationToken.None);
        var remembered = rememberedStore.Load();

        if (devices.Count == 0)
        {
            Console.WriteLine("No USB devices found.");
            return;
        }

        Console.WriteLine($"{"BUS-ID",-8} {"STATE",-10} {"REM",-5} {"VID:PID",-10} DESCRIPTION");
        Console.WriteLine(new string('-', 72));
        foreach (var d in devices)
        {
            var busId = string.IsNullOrEmpty(d.BusId) ? "-" : d.BusId;
            var isRemembered = !string.IsNullOrEmpty(d.InstanceId) && remembered.ContainsKey(d.InstanceId);
            var rem = "no";
            if (isRemembered && remembered.TryGetValue(d.InstanceId, out var rememberedTarget))
                rem = $"yes ({rememberedTarget.Type}:{rememberedTarget.Name})";

            Console.WriteLine($"{busId,-8} {d.State,-10} {rem,-22} {d.HardwareId,-10} {d.Description}");
        }
    }

    private static async Task DistrosCommand()
    {
        var localWsl = new WslUserSpaceInterop();
        var distros = await localWsl.QueryDistrosAsync();
        if (distros.Count == 0)
        {
            Console.WriteLine("No WSL distros found (is WSL installed?).");
            return;
        }

        Console.WriteLine("WSL Distros:");
        foreach (var distro in distros)
        {
            var state = distro.IsRunning ? "running" : "offline";
            Console.WriteLine($"  {distro.Name} ({state})");
        }
    }

    private static async Task AttachCommand(
        BridgeServiceClient client,
        LocalDeviceManager deviceManager,
        string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: attach <bus-id> [--wsl <distro> | --ssh <host>]");
            Environment.Exit(1);
        }

        var busId = args[0];
        var target = ParseAttachTarget(args.Skip(1).ToArray());

        Console.WriteLine($"Attaching {busId} → {target.Type}:{(string.IsNullOrWhiteSpace(target.Name) ? "<default>" : target.Name)}...");

        // Bind via service first (requires elevation).
        var bindResp = await client.Admin.BindDeviceAsync(new BindDeviceRequest { BusId = busId });
        if (!bindResp.Ok)
        {
            Console.WriteLine($"Bind FAILED: {bindResp.Message}");
            Environment.Exit(1);
        }

        // Attach via app (user context, sees WSL distros).
        var (ok, msg) = await deviceManager.AttachAsync(busId, target, CancellationToken.None);
        Console.WriteLine(ok ? "OK" : $"Attach FAILED: {msg}");
        if (!ok) Environment.Exit(1);
    }

    private static async Task DetachCommand(
        BridgeServiceClient client,
        LocalDeviceManager deviceManager,
        string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: detach <bus-id>");
            Environment.Exit(1);
        }

        var busId = args[0];
        Console.WriteLine($"Detaching {busId}...");

        var (detachOk, detachMsg) = await deviceManager.DetachAsync(busId, CancellationToken.None);
        if (!detachOk)
        {
            Console.WriteLine($"Detach FAILED: {detachMsg}");
            Environment.Exit(1);
        }

        var unbindResp = await client.Admin.UnbindDeviceAsync(new UnbindDeviceRequest { BusId = busId });
        Console.WriteLine(unbindResp.Ok ? "OK" : $"Detached but unbind failed: {unbindResp.Message}");
    }

    private static void RememberCommand(AppRememberedDeviceStore store, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: remember <instance-id> <target-name> [--target-type wsl|ssh]");
            Environment.Exit(1);
        }

        var type = AttachTargetType.Wsl;
        var maybeTypeFlag = Array.FindIndex(args, a => a.Equals("--target-type", StringComparison.OrdinalIgnoreCase));
        if (maybeTypeFlag >= 0 && maybeTypeFlag + 1 < args.Length
            && args[maybeTypeFlag + 1].Equals("ssh", StringComparison.OrdinalIgnoreCase))
        {
            type = AttachTargetType.Ssh;
        }

        store.AddOrUpdate(args[0], new AttachTarget { Type = type, Name = args[1] });
        Console.WriteLine($"OK: Remembered {args[0]} → {type}:{args[1]}");
    }

    private static void ForgetCommand(AppRememberedDeviceStore store, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: forget <instance-id>");
            Environment.Exit(1);
        }

        var removed = store.Remove(args[0]);
        Console.WriteLine(removed ? $"OK: Forgot {args[0]}" : $"Not found: {args[0]}");
    }

    private static void RememberedCommand(AppRememberedDeviceStore store)
    {
        var remembered = store.Load();
        if (remembered.Count == 0)
        {
            Console.WriteLine("No remembered devices.");
            return;
        }

        Console.WriteLine($"{"INSTANCE-ID",-60} TARGET");
        Console.WriteLine(new string('-', 80));
        foreach (var (instanceId, target) in remembered)
            Console.WriteLine($"{instanceId,-60} {target.Type}:{target.Name}");
    }

    private static AttachTarget ParseAttachTarget(string[] args)
    {
        if (args.Length == 0)
            return new AttachTarget { Type = AttachTargetType.Wsl, Name = string.Empty };

        var wslIndex = Array.FindIndex(args, a => a.Equals("--wsl", StringComparison.OrdinalIgnoreCase));
        if (wslIndex >= 0)
        {
            var distro = wslIndex + 1 < args.Length ? args[wslIndex + 1] : string.Empty;
            return new AttachTarget { Type = AttachTargetType.Wsl, Name = distro };
        }

        var sshIndex = Array.FindIndex(args, a => a.Equals("--ssh", StringComparison.OrdinalIgnoreCase));
        if (sshIndex >= 0)
        {
            var host = sshIndex + 1 < args.Length ? args[sshIndex + 1] : string.Empty;
            return new AttachTarget { Type = AttachTargetType.Ssh, Name = host };
        }

        return new AttachTarget { Type = AttachTargetType.Wsl, Name = args[0] };
    }

    private static void PrintHelp(string serviceAddress)
    {
        Console.WriteLine("USB Device Bridge — Test Client");
        Console.WriteLine($"Service: {serviceAddress}");
        Console.WriteLine();
        Console.WriteLine("USAGE");
        Console.WriteLine("  dotnet run --project src/UsbDeviceBridge.App -- <command> [args]");
        Console.WriteLine("  ./scripts/dev.ps1 <command>");
        Console.WriteLine();
        Console.WriteLine("COMMANDS");
        Console.WriteLine("  devices (d)                       List all USB devices");
        Console.WriteLine("  distros                           List WSL distros");
        Console.WriteLine("  attach (a) <bus-id> [--wsl <distro> | --ssh <host>]  Bind + attach device");
        Console.WriteLine("  detach (x) <bus-id>               Detach + unbind device");
        Console.WriteLine("  remember (r) <instance-id> <target-name> [--target-type wsl|ssh]  Remember for auto-attach");
        Console.WriteLine("  forget (f) <instance-id>          Forget device");
        Console.WriteLine("  remembered (rm)                   List remembered devices");
        Console.WriteLine();
        Console.WriteLine("  No args → open the UI window");
    }
}

