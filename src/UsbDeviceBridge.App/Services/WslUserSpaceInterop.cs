using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace UsbDeviceBridge.App.Services;

public readonly record struct LocalWslDistro(string Name, bool IsRunning, string Version);

public readonly record struct LocalProcessResult(int ExitCode, string StdOut, string StdErr);

public sealed partial class WslUserSpaceInterop
{
    public async Task<IReadOnlyList<LocalWslDistro>> QueryDistrosAsync(CancellationToken cancellationToken = default)
    {
        var verbose = await RunWslAsync(["--list", "--verbose"], utf16Output: true, cancellationToken);
        if (verbose.ExitCode == 0)
        {
            var parsedVerbose = ParseVerbose(verbose.StdOut);
            if (parsedVerbose.Count > 0)
                return parsedVerbose;
        }

        var quiet = await RunWslAsync(["--list", "--quiet"], utf16Output: true, cancellationToken);
        if (quiet.ExitCode != 0)
            return [];

        var names = ParseQuiet(quiet.StdOut);
        return names
            .Select(name => new LocalWslDistro(name, IsRunning: true, Version: "2"))
            .ToArray();
    }

    public async Task<int> RunCommandInDistroStreamingAsync(
        string distroName,
        string command,
        Func<string, bool, Task> onLine,
        CancellationToken cancellationToken = default,
        string? user = null
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(distroName);

        if (!string.IsNullOrWhiteSpace(user))
        {
            psi.ArgumentList.Add("-u");
            psi.ArgumentList.Add(user);
        }

        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(command);

        using var process = Process.Start(psi);
        if (process is null)
        {
            await onLine("Failed to start wsl.exe process.", true);
            return -1;
        }

        var stdoutTask = PumpLinesAsync(process.StandardOutput, isError: false, onLine, cancellationToken);
        var stderrTask = PumpLinesAsync(process.StandardError, isError: true, onLine, cancellationToken);

        await process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(stdoutTask, stderrTask);

        return process.ExitCode;
    }

    private static async Task PumpLinesAsync(
        StreamReader reader,
        bool isError,
        Func<string, bool, Task> onLine,
        CancellationToken cancellationToken
    )
    {
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            var clean = line.Replace("\0", string.Empty);
            if (clean.Length == 0)
                continue;

            await onLine(clean, isError);
        }
    }

    private static async Task<LocalProcessResult> RunWslAsync(
        IReadOnlyList<string> arguments,
        bool utf16Output,
        CancellationToken cancellationToken
    )
    {
        var psi = new ProcessStartInfo
        {
            FileName = "wsl.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = utf16Output ? Encoding.Unicode : null,
            StandardErrorEncoding = Encoding.Unicode,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            return new LocalProcessResult(-1, string.Empty, "Failed to start wsl.exe process.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = (await stdoutTask).Replace("\0", string.Empty).TrimStart('\uFEFF').Trim();
        var stderr = (await stderrTask).Replace("\0", string.Empty).TrimStart('\uFEFF').Trim();

        return new LocalProcessResult(process.ExitCode, stdout, stderr);
    }

    private static IReadOnlyList<LocalWslDistro> ParseVerbose(string stdout)
    {
        var result = new List<LocalWslDistro>();

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("*", StringComparison.Ordinal))
                line = line[1..].TrimStart();

            var columns = MultiSpaceSeparatorRegex().Split(line);
            if (columns.Length == 0)
                continue;

            var name = columns[0].Trim();
            if (name.Length == 0)
                continue;

            var isRunning = columns.Length > 1 &&
                string.Equals(columns[1].Trim(), "running", StringComparison.OrdinalIgnoreCase);
            var version = columns.Length > 2 ? columns[2].Trim() : "2";

            result.Add(new LocalWslDistro(name, isRunning, version));
        }

        return result;
    }

    private static IReadOnlyList<string> ParseQuiet(string stdout)
    {
        var names = new List<string>();

        foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("*", StringComparison.Ordinal))
                line = line[1..].TrimStart();

            if (line.Length > 0)
                names.Add(line);
        }

        return names
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceSeparatorRegex();
}