using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UsbDeviceBridge.App.Shell;

public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\UsbDeviceBridgeNative.Singleton";
    private const string PipeName = "UsbDeviceBridgeNative.Activate";

    private readonly Mutex _mutex;
    private CancellationTokenSource? _cts;
    private Task? _listenerTask;

    public SingleInstanceCoordinator()
    {
        _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        IsPrimaryInstance = createdNew;
    }

    public bool IsPrimaryInstance { get; }

    public event Action? ActivationRequested;

    public void StartListening()
    {
        if (!IsPrimaryInstance || _listenerTask is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _listenerTask = Task.Run(() => ListenLoopAsync(_cts.Token));
    }

    public static async Task NotifyPrimaryInstanceAsync()
    {
        try
        {
            using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            await client.ConnectAsync(2000);
            await using var writer = new StreamWriter(client) { AutoFlush = true };
            await writer.WriteLineAsync("activate");
        }
        catch
        {
            // Best effort foreground signal.
        }
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var message = await reader.ReadLineAsync();

                if (string.Equals(message, "activate", StringComparison.OrdinalIgnoreCase))
                {
                    ActivationRequested?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Keep listener alive across transient errors.
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        // Allow other user contexts on the same desktop session to send activate.
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            PipeName,
            PipeDirection.In,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            0,
            0,
            security);
    }

    public void Dispose()
    {
        _cts?.Cancel();
        try
        {
            _listenerTask?.Wait(TimeSpan.FromMilliseconds(200));
        }
        catch
        {
            // Ignore shutdown race.
        }
        _cts?.Dispose();

        if (IsPrimaryInstance)
        {
            _mutex.ReleaseMutex();
        }
        _mutex.Dispose();
    }
}