using System.Diagnostics;
using Grpc.Core;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Setup;

/// <summary>
/// Handles process execution for setup operations and stream output writing.
/// </summary>
public interface ISetupProcessRunner
{
    Task<(int Code, string? StdOut, string? StdErr)> RunProcessAsync(
        string fileName,
        string[] args,
        CancellationToken ct
    );

    Task WriteOutputLineAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        string output,
        bool isError
    );
}

/// <summary>
/// Concrete implementation of <see cref="ISetupProcessRunner"/>.
/// </summary>
internal sealed class SetupProcessRunner(ILogger<SetupProcessRunner> logger) : ISetupProcessRunner
{
    public async Task<(int Code, string? StdOut, string? StdErr)> RunProcessAsync(
        string fileName,
        string[] args,
        CancellationToken ct
    )
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
                return (-1, null, "Failed to start process");

            await process.WaitForExitAsync(ct);

            var stdout = await process.StandardOutput.ReadToEndAsync(ct);
            var stderr = await process.StandardError.ReadToEndAsync(ct);

            return (process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error running process {FileName}", fileName);
            return (-1, null, ex.Message);
        }
    }

    public async Task WriteOutputLineAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        string output,
        bool isError
    )
    {
        // Split multi-line output and write each line separately.
        var lines = output.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
                continue;

            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = line,
                IsError = isError,
                ExitCode = 0,
            });
        }
    }
}
