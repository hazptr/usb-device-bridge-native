namespace UsbDeviceBridge.Service.Interop;

/// <summary>
/// High-level distro query and validation logic built on top of
/// <see cref="WslExeExecutor"/> and <see cref="WslApiNativeMethods"/>.
/// All methods are static.
/// </summary>
internal static class WslDistroQueryService
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    internal static Task<ProcessResult> ListDistrosVerboseAsync(CancellationToken cancellationToken)
        // wsl.exe outputs UTF-16 LE for --list; encoding is set in WslExeExecutor.
        => WslExeExecutor.RunWslExeAsync("--list --verbose", cancellationToken, utf16Output: true);

    internal static Task<ProcessResult> ListDistrosQuietAsync(CancellationToken cancellationToken)
        => WslExeExecutor.RunWslExeAsync("--list --quiet", cancellationToken, utf16Output: true);

    internal static Task<ProcessResult> ListRunningDistrosQuietAsync(CancellationToken cancellationToken)
        => WslExeExecutor.RunWslExeAsync("--list --running --quiet", cancellationToken, utf16Output: true);

    internal static Task<ProcessResult> TerminateDistroAsync(
        string distroName,
        CancellationToken cancellationToken
    ) => WslExeExecutor.RunWslExeAsync($"--terminate \"{distroName}\"", cancellationToken);

    internal static async Task<IReadOnlyList<string>> QuerySelectableDistrosAsync(
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

        return WslDistroParser.BuildSelectableDistros(validInstalled, runningValid);
    }

    private static (bool IsValid, bool ApiAvailable) TryValidateDistroWithWslApi(string distroName)
    {
        IntPtr envVars = IntPtr.Zero;
        try
        {
            var hr = WslApiNativeMethods.WslGetDistributionConfiguration(
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
                    WslApiNativeMethods.WslFreeMemory(envVars);
                }
                catch (EntryPointNotFoundException)
                {
                    // Some wslapi versions do not export WslFreeMemory.
                    // In that case we skip explicit free and rely on process cleanup.
                }
            }
        }
    }
}
