using System.Diagnostics;

namespace UsbDeviceBridge.Service.Interop;

public interface ICommandRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken);
}

public sealed class CommandRunner(ILogger<CommandRunner> logger) : ICommandRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            using var process = Process.Start(startInfo);
            if (process is null)
                return new ProcessResult(-1, string.Empty, "Failed to start process.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new ProcessResult(process.ExitCode, await stdoutTask, await stderrTask);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Version command failed for {FileName}", fileName);
            return new ProcessResult(-1, string.Empty, ex.Message);
        }
    }
}