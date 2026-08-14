using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace UsbDeviceBridge.App.Services;

/// <summary>
/// Records the <c>ssh -N -R</c> processes this app spawns so a later run can kill any that
/// outlived it.
/// </summary>
/// <remarks>
/// Windows does not terminate child processes with their parent, so a crash or a force-kill
/// leaves the reverse tunnel running with nothing tracking it. The tunnel then holds the
/// remote's forwarded port while the app that could release it is gone.
/// <para>
/// Orphans are matched on process id <em>and</em> start time. A pid on its own is not an
/// identity — it can be reused by an unrelated process between runs — and matching on the
/// command line instead would risk killing reverse tunnels the user set up themselves.
/// </para>
/// </remarks>
public sealed class SshTunnelRegistry
{
    /// <summary>Tolerance for the recorded start time, covering JSON round-trip rounding.</summary>
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    private const string SshProcessName = "ssh";

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    public SshTunnelRegistry(string? path = null, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        _path = path ?? DefaultPath();
    }

    private static string DefaultPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(appData, "UsbDeviceBridge", "ssh-tunnels.json");
    }

    /// <summary>One spawned tunnel process.</summary>
    public sealed record TunnelRecord(int Pid, DateTime StartTimeUtc, string Host);

    /// <summary>Adds a freshly spawned tunnel, replacing any earlier entry for the same pid.</summary>
    public void Record(int pid, DateTime startTimeUtc, string host)
    {
        lock (_gate)
        {
            var records = Read();
            records.RemoveAll(r => r.Pid == pid);
            records.Add(new TunnelRecord(pid, startTimeUtc, host));
            Write(records);
        }
    }

    /// <summary>Drops a tunnel this app has already stopped.</summary>
    public void Forget(int pid)
    {
        lock (_gate)
        {
            var records = Read();
            if (records.RemoveAll(r => r.Pid == pid) > 0)
                Write(records);
        }
    }

    /// <summary>
    /// Kills every recorded tunnel that is still alive and clears the record.
    /// Call once at startup, before any tunnel of this run is created.
    /// </summary>
    /// <returns>How many orphaned processes were killed.</returns>
    public int SweepOrphans()
    {
        lock (_gate)
        {
            var records = Read();
            if (records.Count == 0)
                return 0;

            var killed = 0;
            foreach (var record in records)
            {
                if (TryKill(record))
                    killed++;
            }

            // Cleared unconditionally: anything left is either dead or unidentifiable, and
            // keeping it would make every future sweep retry the same stale entries.
            Write([]);

            if (killed > 0)
                _logger.LogInformation("Killed {Count} orphaned SSH reverse tunnel(s) from a previous run.", killed);

            return killed;
        }
    }

    private bool TryKill(TunnelRecord record)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(record.Pid);
        }
        catch (ArgumentException)
        {
            // Already gone — the normal case.
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not inspect pid {Pid} while sweeping tunnels.", record.Pid);
            return false;
        }

        using (process)
        {
            if (!IsSameProcess(process, record))
                return false;

            try
            {
                process.Kill(entireProcessTree: true);
                _logger.LogInformation(
                    "Killed orphaned SSH tunnel to '{Host}' (pid {Pid}) left by a previous run.",
                    record.Host, record.Pid);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not kill orphaned SSH tunnel pid {Pid}.", record.Pid);
                return false;
            }
        }
    }

    /// <summary>
    /// Confirms the live process really is the one recorded, so a reused pid is never killed.
    /// </summary>
    private bool IsSameProcess(Process process, TunnelRecord record)
    {
        try
        {
            if (!string.Equals(process.ProcessName, SshProcessName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Pid {Pid} is now '{Name}', not ssh; leaving it alone.", record.Pid, process.ProcessName);
                return false;
            }

            var actualStart = process.StartTime.ToUniversalTime();
            var drift = (actualStart - record.StartTimeUtc).Duration();
            if (drift > StartTimeTolerance)
            {
                _logger.LogDebug(
                    "Pid {Pid} started at {Actual:o}, expected {Expected:o}; treating as a reused pid.",
                    record.Pid, actualStart, record.StartTimeUtc);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            // Access denied on another user's process, or it exited mid-check.
            _logger.LogDebug(ex, "Could not verify identity of pid {Pid}.", record.Pid);
            return false;
        }
    }

    /// <summary>Test hook: the currently persisted records.</summary>
    internal List<TunnelRecord> ReadRecordsForTest()
    {
        lock (_gate)
            return Read();
    }

    private List<TunnelRecord> Read()
    {
        try
        {
            if (!File.Exists(_path))
                return [];

            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
                return [];

            return JsonSerializer.Deserialize<List<TunnelRecord>>(json) ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the SSH tunnel registry; starting empty.");
            return [];
        }
    }

    private void Write(List<TunnelRecord> records)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(_path, JsonSerializer.Serialize(records));
        }
        catch (Exception ex)
        {
            // A registry we cannot persist only costs orphan cleanup; never fail the app.
            _logger.LogWarning(ex, "Could not persist the SSH tunnel registry.");
        }
    }
}
