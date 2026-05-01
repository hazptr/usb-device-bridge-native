using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace UsbDeviceBridge.Service.Interop;

public readonly record struct SelectableWslDistro(string Name, bool IsRunning);

public sealed class WslInterop
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public Task<ProcessResult> RunCommandInDistroAsync(
        string distroName,
        string command,
        CancellationToken cancellationToken,
        string? user = null
    )
    {
        // Prefix with sudo when root is required. Not using sudo -n so that a missing
        // NOPASSWD entry produces a clear auth failure rather than silently failing.
        var fullCommand = user is "root" ? $"sudo {command}" : command;

        // wsl.exe -d fails with WSL_E_DISTRO_NOT_FOUND from this process context;
        // WslLaunch() from wslapi.dll works reliably for in-process distro execution.
        try
        {
            return RunCommandViaWslApiAsync(distroName, fullCommand, cancellationToken);
        }
        catch (DllNotFoundException)
        {
            // wslapi.dll not available (very old Windows) — fall back to wsl.exe
            var userArg = user is not null ? $"-u \"{user}\" " : string.Empty;
            return RunWslExeAsync($"-d \"{distroName}\" {userArg}-- {command}", cancellationToken, createNoWindow: false);
        }
    }

    /// <summary>
    /// Runs a command in the specified WSL distro and calls <paramref name="onLine"/> for each
    /// output line as it arrives, enabling real-time streaming to callers.
    /// stderr is merged into stdout so all output appears in order.
    /// Returns the process exit code.
    /// </summary>
    public async Task<int> RunCommandInDistroStreamingAsync(
        string distroName,
        string command,
        Func<string, Task> onLine,
        CancellationToken cancellationToken,
        string? user = null
    )
    {
        // wsl.exe -d fails with WSL_E_DISTRO_NOT_FOUND from this process context;
        // WslLaunch() from wslapi.dll works reliably.
        // For root: temporarily set distro default UID to 0 so the command runs as root
        // without sudo (avoiding any PTY allocation that could block pipe output).
        if (user is "root")
            return await StreamAsRootViaWslApiAsync(distroName, command, onLine, cancellationToken);

        return await StreamViaWslApiAsync(distroName, command, onLine, cancellationToken);
    }

    private async Task<int> StreamAsRootViaWslApiAsync(
        string distroName,
        string command,
        Func<string, Task> onLine,
        CancellationToken cancellationToken
    )
    {
        var hr = WslGetDistributionConfiguration(
            distroName, out _, out var originalUid, out var flags, out var envVars, out _);
        if (envVars != IntPtr.Zero)
            try { WslFreeMemory(envVars); } catch { }
        if (hr < 0)
            throw new ExternalException(
                $"WslGetDistributionConfiguration failed for '{distroName}' (HRESULT 0x{(uint)hr:X8}).", hr);

        WslConfigureDistribution(distroName, 0, flags);
        try
        {
            return await StreamViaWslApiAsync(distroName, command, onLine, cancellationToken);
        }
        finally
        {
            WslConfigureDistribution(distroName, originalUid, flags);
        }
    }

    private static async Task<int> StreamViaWslApiAsync(
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
        // them into the WSL host process for the Linux process to use. Without this,
        // WslLaunch silently ignores the handles and the process writes to its own terminal.
        var saInheritable = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
        };

        if (!CreatePipe(out var stdoutRead, out var stdoutWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed");
        // stdoutRead is our (parent) end — strip the inherit flag so wsl.exe child
        // processes spawned later don't accidentally inherit it and keep the pipe open.
        SetHandleInformation(stdoutRead, HandleFlagInherit, 0);

        if (!CreatePipe(out var stdinRead, out var stdinWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed");
        SetHandleInformation(stdinWrite, HandleFlagInherit, 0);
        stdinWrite.Close();

        var hr = WslLaunch(distroName, wrappedCommand, useCurrentWorkingDirectory: false,
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
            CloseHandle(processHandle);
        }

        return exitCode;
    }

    private static async Task<ProcessResult> RunCommandViaWslApiAsync(
        string distroName,
        string command,
        CancellationToken cancellationToken
    )
    {
        // Embed the exit code as a sentinel line in stdout because the WslLaunch
        // process handle does not reliably signal when the Linux process exits.
        const string exitSentinel = "__WSL_EXIT_CODE__:";
        var escapedCmd = command.Replace("'", "'\\''" );
        var wrappedCommand = $"sh -c '{escapedCmd}; echo {exitSentinel}$?'";

        // Pipe handles passed to WslLaunch must be inheritable — same reason as
        // StreamViaWslApiAsync. Parent-side read ends have inheritance stripped so
        // they don't leak into wsl.exe sub-processes.
        var saInheritable = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            lpSecurityDescriptor = IntPtr.Zero,
            bInheritHandle = true,
        };

        if (!CreatePipe(out var stdoutRead, out var stdoutWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdout) failed");
        SetHandleInformation(stdoutRead, HandleFlagInherit, 0);

        if (!CreatePipe(out var stderrRead, out var stderrWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stderr) failed");
        SetHandleInformation(stderrRead, HandleFlagInherit, 0);

        // stdin: close the write end immediately so the WSL process gets EOF on stdin.
        if (!CreatePipe(out var stdinRead, out var stdinWrite, ref saInheritable, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe (stdin) failed");
        SetHandleInformation(stdinWrite, HandleFlagInherit, 0);
        stdinWrite.Close();

        var hr = WslLaunch(distroName, wrappedCommand, useCurrentWorkingDirectory: false,
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
            CloseHandle(processHandle);
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

    public Task<ProcessResult> ListDistrosVerboseAsync(CancellationToken cancellationToken)
    {
        // wsl.exe outputs UTF-16 LE for --list; encoding is set in RunWslExeAsync.
        return RunWslExeAsync("--list --verbose", cancellationToken, utf16Output: true);
    }

    public Task<ProcessResult> ListDistrosQuietAsync(CancellationToken cancellationToken)
    {
        return RunWslExeAsync("--list --quiet", cancellationToken, utf16Output: true);
    }

    public Task<ProcessResult> ListRunningDistrosQuietAsync(CancellationToken cancellationToken)
    {
        return RunWslExeAsync("--list --running --quiet", cancellationToken, utf16Output: true);
    }

    public async Task<IReadOnlyList<string>> QuerySelectableDistrosAsync(
        CancellationToken cancellationToken
    )
    {
        var distros = await QuerySelectableDistrosWithStateAsync(cancellationToken);
        return distros.Select(d => d.Name).ToArray();
    }

    public async Task<IReadOnlyList<SelectableWslDistro>> QuerySelectableDistrosWithStateAsync(
        CancellationToken cancellationToken
    )
    {
        var installedResult = await ListDistrosQuietAsync(cancellationToken);
        if (installedResult.ExitCode != 0)
            return [];

        var installedDistros = WslDistroParser.ParseQuiet(installedResult.StdOut);
        if (installedDistros.Count == 0)
            return [];

        var validDistros = new HashSet<string>(NameComparer);
        var isWslApiAvailable = true;
        foreach (var distro in installedDistros)
        {
            var validate = TryValidateDistroWithWslApi(distro);
            if (!validate.ApiAvailable)
            {
                isWslApiAvailable = false;
                break;
            }

            if (validate.IsValid)
                validDistros.Add(distro);
        }

        if (!isWslApiAvailable || validDistros.Count == 0)
        {
            validDistros.Clear();
            foreach (var distro in installedDistros)
                validDistros.Add(distro);
        }

        var runningResult = await ListRunningDistrosQuietAsync(cancellationToken);
        IReadOnlyList<string> runningDistros;
        if (runningResult.ExitCode == 0)
        {
            runningDistros = WslDistroParser.ParseQuiet(runningResult.StdOut);
        }
        else
        {
            // Some WSL builds may not support --running --quiet together.
            var verboseResult = await ListDistrosVerboseAsync(cancellationToken);
            if (verboseResult.ExitCode != 0)
                return [];

            runningDistros = WslDistroParser
                .ParseVerbose(verboseResult.StdOut)
                .Where(entry => entry.RuntimeState == WslDistroRuntimeState.Running)
                .Select(entry => entry.Name)
                .ToArray();
        }

        var validInstalled = installedDistros
            .Where(validDistros.Contains)
            .ToArray();

        var runningValid = runningDistros
            .Where(validDistros.Contains)
            .ToArray();

        var ordered = WslDistroParser.BuildSelectableDistros(validInstalled, runningValid);
        var runningSet = new HashSet<string>(runningValid, NameComparer);

        return ordered
            .Select(name => new SelectableWslDistro(name, runningSet.Contains(name)))
            .ToArray();
    }

    public Task<ProcessResult> TerminateDistroAsync(
        string distroName,
        CancellationToken cancellationToken
    )
    {
        return RunWslExeAsync($"--terminate \"{distroName}\"", cancellationToken);
    }

    private static async Task<ProcessResult> RunWslExeAsync(
        string args,
        CancellationToken cancellationToken,
        bool utf16Output = false,
        bool createNoWindow = true
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = createNoWindow,
            // wsl --list outputs UTF-16 LE; other commands use the default (UTF-8).
            StandardOutputEncoding = utf16Output ? Encoding.Unicode : null,
            // wsl.exe always emits its own error messages (e.g. "no distribution")
            // in UTF-16 LE regardless of the command, so always decode stderr as UTF-16.
            StandardErrorEncoding = Encoding.Unicode,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        // wsl.exe meta-errors on stderr are always UTF-16 LE; null chars can appear
        // if the encoding doesn't match, so strip them from both streams.
        var stdout = (await stdoutTask).Replace("\0", string.Empty);
        var stderr = (await stderrTask).Replace("\0", string.Empty);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static (bool IsValid, bool ApiAvailable) TryValidateDistroWithWslApi(string distroName)
    {
        IntPtr envVars = IntPtr.Zero;
        try
        {
            var hr = WslGetDistributionConfiguration(
                distroName,
                out _,
                out _,
                out _,
                out envVars,
                out _
            );

            return (hr >= 0, true);
        }
        catch (DllNotFoundException)
        {
            return (false, false);
        }
        catch (EntryPointNotFoundException)
        {
            return (false, false);
        }
        finally
        {
            if (envVars != IntPtr.Zero)
            {
                try
                {
                    WslFreeMemory(envVars);
                }
                catch (EntryPointNotFoundException)
                {
                    // Some wslapi versions do not export WslFreeMemory.
                    // In that case we skip explicit free and rely on process cleanup.
                }
            }
        }
    }

    [Flags]
    private enum WslDistributionFlags : uint
    {
        None = 0,
        EnableInterop = 1,
        AppendNtPath = 2,
        EnableDriveMounting = 4,
    }

    [DllImport("wslapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WslConfigureDistribution(
        string distributionName,
        uint defaultUID,
        WslDistributionFlags wslDistributionFlags
    );

    [DllImport("wslapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WslGetDistributionConfiguration(
        string distributionName,
        out uint distributionVersion,
        out uint defaultUid,
        out WslDistributionFlags wslDistributionFlags,
        out IntPtr defaultEnvironmentVariables,
        out uint defaultEnvironmentVariableCount
    );

    [DllImport("wslapi.dll", CharSet = CharSet.Unicode)]
    private static extern int WslLaunch(
        string distributionName,
        string command,
        [MarshalAs(UnmanagedType.Bool)] bool useCurrentWorkingDirectory,
        SafeFileHandle stdIn,
        SafeFileHandle stdOut,
        SafeFileHandle stdErr,
        out IntPtr process
    );

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    private const uint HandleFlagInherit = 0x00000001;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out SafeFileHandle hReadPipe,
        out SafeFileHandle hWritePipe,
        ref SecurityAttributes lpPipeAttributes,
        int nSize
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(
        SafeFileHandle hObject,
        uint dwMask,
        uint dwFlags
    );

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("wslapi.dll")]
    private static extern void WslFreeMemory(IntPtr memoryPointer);
}

public readonly record struct ProcessResult(int ExitCode, string StdOut, string StdErr);
