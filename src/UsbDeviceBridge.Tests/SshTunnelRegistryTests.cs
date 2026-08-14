using System.Diagnostics;
using UsbDeviceBridge.App.Services;

namespace UsbDeviceBridge.Tests;

public sealed class SshTunnelRegistryTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"udb-tunnels-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    private SshTunnelRegistry Create() => new(_path);

    [Fact]
    public void Record_ThenForget_LeavesNothingBehind()
    {
        var registry = Create();
        registry.Record(4242, DateTime.UtcNow, "desktop");
        Assert.Single(registry.ReadRecordsForTest());

        registry.Forget(4242);
        Assert.Empty(registry.ReadRecordsForTest());
    }

    [Fact]
    public void Record_ReplacesAnEarlierEntryForTheSamePid()
    {
        var registry = Create();
        registry.Record(4242, DateTime.UtcNow, "desktop");
        registry.Record(4242, DateTime.UtcNow, "laptop");

        var record = Assert.Single(registry.ReadRecordsForTest());
        Assert.Equal("laptop", record.Host);
    }

    [Fact]
    public void SweepOrphans_WithNoRecords_DoesNothing()
    {
        Assert.Equal(0, Create().SweepOrphans());
    }

    [Fact]
    public void SweepOrphans_IgnoresPidsThatNoLongerExist()
    {
        var registry = Create();
        // Start and reap a process so its pid is almost certainly free.
        using var dead = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;
        dead.WaitForExit();

        registry.Record(dead.Id, dead.StartTime.ToUniversalTime(), "desktop");

        Assert.Equal(0, registry.SweepOrphans());
        Assert.Empty(registry.ReadRecordsForTest());
    }

    [Fact]
    public void SweepOrphans_LeavesAProcessThatIsNotSsh_Alive()
    {
        var registry = Create();
        using var innocent = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
        })!;

        try
        {
            // A pid recorded by us, but the live process is cmd.exe, not ssh: killing it
            // would mean a reused pid took out an unrelated process.
            registry.Record(innocent.Id, innocent.StartTime.ToUniversalTime(), "desktop");

            Assert.Equal(0, registry.SweepOrphans());
            innocent.Refresh();
            Assert.False(innocent.HasExited);
        }
        finally
        {
            if (!innocent.HasExited)
                innocent.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SweepOrphans_LeavesAProcessWhoseStartTimeDoesNotMatch_Alive()
    {
        var registry = Create();
        using var innocent = Process.Start(new ProcessStartInfo("cmd.exe", "/c pause")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardInput = true,
        })!;

        try
        {
            // Same pid, wildly different start time — the signature of pid reuse.
            registry.Record(innocent.Id, new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), "desktop");

            Assert.Equal(0, registry.SweepOrphans());
            innocent.Refresh();
            Assert.False(innocent.HasExited);
        }
        finally
        {
            if (!innocent.HasExited)
                innocent.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void SweepOrphans_ClearsRecordsEvenWhenNothingWasKilled()
    {
        var registry = Create();
        registry.Record(999_999, DateTime.UtcNow, "desktop");

        registry.SweepOrphans();

        // Stale entries must not accumulate, or every later sweep rechecks dead pids.
        Assert.Empty(registry.ReadRecordsForTest());
    }

    [Fact]
    public void Read_ToleratesACorruptRegistryFile()
    {
        File.WriteAllText(_path, "{ this is not json");

        var registry = Create();
        Assert.Empty(registry.ReadRecordsForTest());
        Assert.Equal(0, registry.SweepOrphans());
    }
}
