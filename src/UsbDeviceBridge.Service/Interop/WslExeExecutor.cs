using System.Diagnostics;
using System.Text;

namespace UsbDeviceBridge.Service.Interop;

/// <summary>
/// Executes commands via the wsl.exe process. All methods are static.
/// </summary>
internal static class WslExeExecutor
{
    /// <summary>
    /// Runs wsl.exe with the specified arguments and returns the full output.
    /// </summary>
    /// <param name="args">Arguments to pass to wsl.exe.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <param name="utf16Output">
    ///   When <see langword="true"/>, stdout is decoded as UTF-16 LE
    ///   (required for <c>wsl --list</c> commands).
    /// </param>
    /// <param name="createNoWindow">Whether to suppress the console window.</param>
    internal static async Task<ProcessResult> RunWslExeAsync(
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
            // wsl.exe always emits its own error messages in UTF-16 LE regardless of the command.
            StandardErrorEncoding = Encoding.Unicode,
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        // Null chars can appear if the encoding doesn't match, so strip them from both streams.
        var stdout = (await stdoutTask).Replace("\0", string.Empty);
        var stderr = (await stderrTask).Replace("\0", string.Empty);

        return new ProcessResult(process.ExitCode, stdout, stderr);
    }
}
