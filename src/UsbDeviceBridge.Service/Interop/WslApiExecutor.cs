using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace UsbDeviceBridge.Service.Interop;

/// <summary>
/// Pipe-based execution of commands in WSL distros via wslapi.dll WslLaunch.
/// All methods are static; there is no instance state.
/// </summary>
internal static class WslApiExecutor
{
    /// <summary>
    /// Temporarily elevates the distro's default UID to root, runs the command via
    /// <see cref="StreamViaWslApiAsync"/>, then restores the original UID.
    /// </summary>
    internal static async Task<int> StreamAsRootViaWslApiAsync(
        string distroName,
        string command,
        Func<string, Task> onLine,
        CancellationToken cancellationToken
    )
    {
        var hr = WslApiNativeMethods.WslGetDistributionConfiguration(
            distroName, out _, out var originalUid, out var flags, out var envVars, out _);
        if (envVars != IntPtr.Zero)
            try { WslApiNativeMethods.WslFreeMemory(envVars); } catch { }
        if (hr < 0)
            throw new ExternalException(
                $"WslGetDistributionConfiguration failed for '{distroName}' (HRESULT 0x{(uint)hr:X8}).", hr);

        WslApiNativeMethods.WslConfigureDistribution(distroName, 0, flags);
        try
        {
            return await StreamViaWslApiAsync(distroName, command, onLine, cancellationToken);
        }
        finally
        {
            WslApiNativeMethods.WslConfigureDistribution(distroName, originalUid, flags);
        }
    }

    /// <summary>
    /// Streams command output line-by-line from a WSL distro using WslLaunch pipes.
    /// stderr is merged into stdout so all output arrives in order.
    /// Returns the process exit code.
    /// </summary>
    internal static async Task<int> StreamViaWslApiAsync(
        string distroName,
        string command,
        Func<string, Task> onLine,
        CancellationToken cancellationToken
    )
    {
        const string exitSentinel = "__WSL_EXIT_CODE__:";
        var escapedCmd = command.Replace("'", "'\\''");
        // Merge stderr into stdout (2>&1) so all output arrives on one stream.
        var wrappedCommand = $"sh -c '{escapedCmd} 2>&1; echo {exitSentinel}$?'";

        // Pipe handles passed to WslLaunch must be inheritable so WslLaunch can duplicate
        // them into the WSL host process for the Linux process to use.
        var saInheritable = new WslApiNativeMethods.SecurityAttributes
        {
            nLength = Marshal.SizeOf<WslApiNativeMethods.SecurityAttributes>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
        };

        if (!WslApiNativeMethods.CreatePipe(out var stdoutRead, out var stdoutWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed");
        // Strip the inherit flag on the parent-side read end so wsl.exe children don't hold it open.
        WslApiNativeMethods.SetHandleInformation(stdoutRead, WslApiNativeMethods.HandleFlagInherit, 0);

        if (!WslApiNativeMethods.CreatePipe(out var stdinRead, out var stdinWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed");
        WslApiNativeMethods.SetHandleInformation(stdinWrite, WslApiNativeMethods.HandleFlagInherit, 0);
        stdinWrite.Close();

        var hr = WslApiNativeMethods.WslLaunch(distroName, wrappedCommand, useCurrentWorkingDirectory: false,
            stdinRead, stdoutWrite, stdoutWrite, out var processHandle);
        stdinRead.Close();
        stdoutWrite.Close();

        if (hr < 0)
            throw new ExternalException(
                $"WslLaunch failed for '{distroName}' (HRESULT 0x{(uint)hr:X8}).", hr);

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });

        // Blocking ReadLine on a thread-pool thread; async consumer below.
        var producer = Task.Run(() =>
        {
            try
            {
                using var stream = new FileStream(stdoutRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) is not null)
                    channel.Writer.TryWrite(line);
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, CancellationToken.None);

        // Closing the read handle unblocks the producer when cancellation is requested.
        using var cancelReg = cancellationToken.Register(() =>
        {
            try { stdoutRead.Close(); } catch { }
        });

        int exitCode = 0;
        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (line.StartsWith(exitSentinel, StringComparison.Ordinal))
                    int.TryParse(line[exitSentinel.Length..].Trim(), out exitCode);
                else
                    await onLine(line.Replace("\0", string.Empty));
            }
        }
        finally
        {
            await producer;
            WslApiNativeMethods.CloseHandle(processHandle);
        }

        return exitCode;
    }

    /// <summary>
    /// Runs a command in the specified WSL distro and returns the full output as a
    /// <see cref="ProcessResult"/>. Uses WslLaunch pipes for reliable distro targeting.
    /// </summary>
    internal static async Task<ProcessResult> RunCommandViaWslApiAsync(
        string distroName,
        string command,
        CancellationToken cancellationToken
    )
    {
        // Embed the exit code as a sentinel line in stdout because the WslLaunch
        // process handle does not reliably signal when the Linux process exits.
        const string exitSentinel = "__WSL_EXIT_CODE__:";
        var escapedCmd = command.Replace("'", "'\\''");
        var wrappedCommand = $"sh -c '{escapedCmd}; echo {exitSentinel}$?'";

        var saInheritable = new WslApiNativeMethods.SecurityAttributes
        {
            nLength = Marshal.SizeOf<WslApiNativeMethods.SecurityAttributes>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
        };

        if (!WslApiNativeMethods.CreatePipe(out var stdoutRead, out var stdoutWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed");
        WslApiNativeMethods.SetHandleInformation(stdoutRead, WslApiNativeMethods.HandleFlagInherit, 0);

        if (!WslApiNativeMethods.CreatePipe(out var stderrRead, out var stderrWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stderr) failed");
        WslApiNativeMethods.SetHandleInformation(stderrRead, WslApiNativeMethods.HandleFlagInherit, 0);

        // stdin: close the write end immediately so the WSL process gets EOF on stdin.
        if (!WslApiNativeMethods.CreatePipe(out var stdinRead, out var stdinWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed");
        WslApiNativeMethods.SetHandleInformation(stdinWrite, WslApiNativeMethods.HandleFlagInherit, 0);
        stdinWrite.Close();

        var hr = WslApiNativeMethods.WslLaunch(distroName, wrappedCommand, useCurrentWorkingDirectory: false,
            stdinRead, stdoutWrite, stderrWrite, out var processHandle);

        // We've handed these ends to WSL — close our copies so we get EOF when WSL exits.
        stdinRead.Close();
        stdoutWrite.Close();
        stderrWrite.Close();

        if (hr < 0)
            throw new ExternalException(
                $"WslLaunch failed for '{distroName}' (HRESULT 0x{(uint)hr:X8}). " +
                "The distribution may not be running or is not registered for this user.", hr);

        // Read both streams concurrently on thread-pool threads (anonymous pipes don't
        // support async I/O). Concurrent reads are required to avoid a deadlock if one
        // pipe's buffer fills while we're blocking on the other.
        var stdoutTask = Task.Run(() =>
        {
            using var stream = new FileStream(stdoutRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });

        var stderrTask = Task.Run(() =>
        {
            using var stream = new FileStream(stderrRead, FileAccess.Read, bufferSize: 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });

        // If cancellation is requested, close the read handles to unblock the background reads.
        using var cancelReg = cancellationToken.Register(() =>
        {
            try { stdoutRead.Close(); } catch { }
            try { stderrRead.Close(); } catch { }
        });

        string rawStdout, stderr;
        try
        {
            rawStdout = await stdoutTask;
            stderr = await stderrTask;
        }
        finally
        {
            WslApiNativeMethods.CloseHandle(processHandle);
        }

        // Parse the exit code sentinel from the last line of stdout.
        int exitCode = 0;
        string stdout = rawStdout;
        var sentinelIdx = rawStdout.LastIndexOf(exitSentinel, StringComparison.Ordinal);
        if (sentinelIdx >= 0)
        {
            var afterSentinel = rawStdout[(sentinelIdx + exitSentinel.Length)..].Trim();
            int.TryParse(afterSentinel.Split('\n', '\r')[0], out exitCode);
            stdout = rawStdout[..sentinelIdx];
        }

        return new ProcessResult(exitCode,
            stdout.Replace("\0", string.Empty),
            stderr.Replace("\0", string.Empty));
    }
}
