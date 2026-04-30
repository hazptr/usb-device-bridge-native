using Grpc.Core;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Provides a command-line test harness for the service.
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

        try
        {
            switch (command)
            {
                case "devices" or "d":
                    await DevicesCommand(client);
                    break;
                case "distros":
                    await DistrosCommand(client);
                    break;
                case "attach" or "a":
                    await AttachCommand(client, cmdArgs);
                    break;
                case "detach" or "x":
                    await DetachCommand(client, cmdArgs);
                    break;
                case "remember" or "r":
                    await RememberCommand(client, cmdArgs);
                    break;
                case "forget" or "f":
                    await ForgetCommand(client, cmdArgs);
                    break;
                case "remembered" or "rm":
                    await RememberedCommand(client);
                    break;
                case "stream" or "s":
                    await StreamCommand(client);
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

    private static async Task DevicesCommand(BridgeServiceClient client)
    {
        var resp = await client.Device.GetDevicesAsync(new GetDevicesRequest());
        if (resp.Devices.Count == 0)
        {
            Console.WriteLine("No USB devices found.");
            return;
        }

        Console.WriteLine($"{"BUS-ID",-8} {"STATE",-10} {"REM",-5} {"VID:PID",-10} DESCRIPTION");
        Console.WriteLine(new string('-', 72));
        foreach (var d in resp.Devices)
        {
            var busId = string.IsNullOrEmpty(d.BusId) ? "-" : d.BusId;
            var rem = d.Remembered ? $"yes ({d.PreferredDistro})" : "no";
            Console.WriteLine($"{busId,-8} {d.State,-10} {rem,-22} {d.HardwareId,-10} {d.Description}");
        }
    }

    private static async Task DistrosCommand(BridgeServiceClient client)
    {
        var resp = await client.Device.QueryWslDistrosAsync(new QueryWslDistrosRequest());
        if (resp.Distros.Count == 0)
        {
            Console.WriteLine("No WSL distros found (is WSL installed?).");
            return;
        }
        Console.WriteLine("WSL Distros:");
        foreach (var d in resp.Distros)
            Console.WriteLine($"  {d}");
    }

    private static async Task AttachCommand(BridgeServiceClient client, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: attach <bus-id> <distro> [--remember <instance-id>]");
            Environment.Exit(1);
        }

        var busId = args[0];
        var distro = args[1];
        var rememberIdx = Array.IndexOf(args, "--remember");
        var instanceId = rememberIdx >= 0 && rememberIdx + 1 < args.Length
            ? args[rememberIdx + 1]
            : "";
        var remember = rememberIdx >= 0;

        Console.WriteLine($"Attaching {busId} → {distro}{(remember ? " (will remember)" : "")}...");

        var resp = await client.Device.AttachDeviceAsync(new AttachDeviceRequest
        {
            BusId = busId,
            WslDistro = distro,
            InstanceId = instanceId,
            Remember = remember,
        });

        Console.WriteLine(resp.Ok ? $"OK: {resp.Message}" : $"FAILED: {resp.Message}");
        if (!resp.Ok) Environment.Exit(1);
    }

    private static async Task DetachCommand(BridgeServiceClient client, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: detach <bus-id>");
            Environment.Exit(1);
        }

        Console.WriteLine($"Detaching {args[0]}...");
        var resp = await client.Device.DetachDeviceAsync(
            new DetachDeviceRequest { BusId = args[0] }
        );
        Console.WriteLine(resp.Ok ? "OK" : $"FAILED: {resp.Message}");
        if (!resp.Ok) Environment.Exit(1);
    }

    private static async Task RememberCommand(BridgeServiceClient client, string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: remember <instance-id> <distro>");
            Environment.Exit(1);
        }

        var resp = await client.AutoAttach.RememberDeviceAsync(new RememberDeviceRequest
        {
            InstanceId = args[0],
            PreferredDistro = args[1],
        });
        Console.WriteLine(resp.Ok ? $"OK: {resp.Message}" : $"FAILED: {resp.Message}");
    }

    private static async Task ForgetCommand(BridgeServiceClient client, string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: forget <instance-id>");
            Environment.Exit(1);
        }

        var resp = await client.AutoAttach.ForgetDeviceAsync(
            new ForgetDeviceRequest { InstanceId = args[0] }
        );
        Console.WriteLine(resp.Ok ? $"OK: {resp.Message}" : $"FAILED: {resp.Message}");
    }

    private static async Task RememberedCommand(BridgeServiceClient client)
    {
        var resp = await client.AutoAttach.GetRememberedDevicesAsync(
            new GetRememberedDevicesRequest()
        );
        if (resp.Devices.Count == 0)
        {
            Console.WriteLine("No remembered devices.");
            return;
        }

        Console.WriteLine($"{"INSTANCE-ID",-60} DISTRO");
        Console.WriteLine(new string('-', 80));
        foreach (var d in resp.Devices)
            Console.WriteLine($"{d.InstanceId,-60} {d.PreferredDistro}");
    }

    private static async Task StreamCommand(BridgeServiceClient client)
    {
        Console.WriteLine("Streaming device events (Ctrl+C to stop)...");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using var call = client.Device.StreamDevices(
            new StreamDevicesRequest(),
            cancellationToken: cts.Token
        );

        try
        {
            await foreach (var evt in call.ResponseStream.ReadAllAsync(cts.Token))
            {
                var d = evt.Device;
                var busId = string.IsNullOrEmpty(d.BusId) ? "-" : d.BusId;
                Console.WriteLine($"[{evt.EventType,-8}] {busId,-8} {d.State,-10} {d.Description}");
            }
        }
        catch (OperationCanceledException) { }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled) { }

        Console.WriteLine("Stream ended.");
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
        Console.WriteLine("  devices (d)                    List all USB devices");
        Console.WriteLine("  distros                        List WSL distros");
        Console.WriteLine("  attach (a) <bus-id> <distro>   Attach device to WSL");
        Console.WriteLine("    [--remember <instance-id>]   Also remember for auto-attach");
        Console.WriteLine("  detach (x) <bus-id>            Detach device from WSL");
        Console.WriteLine("  remember (r) <instance-id> <distro>  Remember for auto-attach");
        Console.WriteLine("  forget (f) <instance-id>       Forget device");
        Console.WriteLine("  remembered (rm)                List remembered devices");
        Console.WriteLine("  stream (s)                     Stream device change events");
        Console.WriteLine();
        Console.WriteLine("  No args → open the UI window");
    }
}
