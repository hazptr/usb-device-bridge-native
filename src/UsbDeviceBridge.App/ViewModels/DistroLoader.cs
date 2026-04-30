using System.Collections.ObjectModel;
using System.Windows;
using Grpc.Core;
using UsbDeviceBridge.App.Services;
using Usbdevicebridge.V1;
using WpfApplication = System.Windows.Application;

namespace UsbDeviceBridge.App.ViewModels;

/// <summary>
/// Encapsulates WSL distro loading with backoff and gate logic,
/// keeping the <see cref="MainViewModel"/> focused on device stream coordination.
/// </summary>
internal sealed class DistroLoader(
    BridgeServiceClient client,
    ObservableCollection<string> distros
)
{
    private static readonly TimeSpan ReloadBackoff = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;

    public async Task EnsureLoadedAsync(bool force = false)
    {
        var now = DateTimeOffset.UtcNow;
        if (!force && distros.Count > 0)
            return;

        if (!force && now - _lastAttemptUtc < ReloadBackoff)
            return;

        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        _lastAttemptUtc = DateTimeOffset.UtcNow;

        await _gate.WaitAsync();
        try
        {
            var resp = await client.Device.QueryWslDistrosAsync(new QueryWslDistrosRequest());

            await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                var hadDistros = distros.Count > 0;

                distros.Clear();
                foreach (var d in resp.Distros)
                    distros.Add(d);

                return hadDistros;
            });
        }
        catch (RpcException)
        {
            // Non-fatal; distro list stays empty until the next successful load.
        }
        finally
        {
            _gate.Release();
        }
    }
}
