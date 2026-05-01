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
    ObservableCollection<DistroOption> distros
)
{
    private static readonly TimeSpan ReloadBackoff = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _lastAttemptUtc = DateTimeOffset.MinValue;
    private readonly Dictionary<string, bool> _runningByDistro = new(StringComparer.OrdinalIgnoreCase);

    public bool IsDistroRunning(string? distroName)
    {
        if (string.IsNullOrWhiteSpace(distroName))
            return false;

        lock (_runningByDistro)
        {
            // Default to true for unknown names so older services (without status metadata)
            // do not incorrectly disable attach actions.
            return _runningByDistro.TryGetValue(distroName, out var isRunning) ? isRunning : true;
        }
    }

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
            var nextRunningByDistro = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (resp.DistroStatuses.Count > 0)
            {
                foreach (var status in resp.DistroStatuses)
                {
                    if (string.IsNullOrWhiteSpace(status.Name))
                        continue;

                    nextRunningByDistro[status.Name] = status.IsRunning;
                }
            }

            if (nextRunningByDistro.Count == 0)
            {
                foreach (var name in resp.Distros)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        nextRunningByDistro[name] = true;
                }
            }

            lock (_runningByDistro)
            {
                _runningByDistro.Clear();
                foreach (var (name, isRunning) in nextRunningByDistro)
                    _runningByDistro[name] = isRunning;
            }

            await WpfApplication.Current.Dispatcher.InvokeAsync(() =>
            {
                var hadDistros = distros.Count > 0;

                var orderedNames = new List<string>();
                foreach (var distro in resp.Distros)
                {
                    if (!string.IsNullOrWhiteSpace(distro))
                        orderedNames.Add(distro);
                }

                if (orderedNames.Count == 0)
                {
                    orderedNames.AddRange(nextRunningByDistro.Keys);
                }

                var wanted = new HashSet<string>(orderedNames, StringComparer.OrdinalIgnoreCase);
                for (var i = distros.Count - 1; i >= 0; i--)
                {
                    if (!wanted.Contains(distros[i].Name))
                        distros.RemoveAt(i);
                }

                foreach (var name in orderedNames)
                {
                    var isRunning = nextRunningByDistro.TryGetValue(name, out var running)
                        ? running
                        : true;

                    var existing = distros.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null)
                    {
                        existing.IsRunning = isRunning;
                    }
                    else
                    {
                        distros.Add(new DistroOption { Name = name, IsRunning = isRunning });
                    }
                }

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
