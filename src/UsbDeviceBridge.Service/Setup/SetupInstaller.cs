using Grpc.Core;
using Usbdevicebridge.V1;

namespace UsbDeviceBridge.Service.Setup;

/// <summary>
/// Orchestrates the installation of usbipd-win and WSL2.
/// </summary>
public interface ISetupInstaller
{
    Task RunUsbIpdInstallationAsync(IServerStreamWriter<SetupOutputEvent> responseStream, CancellationToken ct);
    Task RunWslInstallationAsync(IServerStreamWriter<SetupOutputEvent> responseStream, CancellationToken ct);
}

/// <summary>
/// Concrete implementation of <see cref="ISetupInstaller"/>.
/// </summary>
internal sealed class SetupInstaller(
    ILogger<SetupInstaller> logger,
    ISetupProcessRunner processRunner
) : ISetupInstaller
{
    public async Task RunUsbIpdInstallationAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        CancellationToken ct
    )
    {
        var (code, stdout, stderr) = await processRunner.RunProcessAsync(
            "winget",
            ["install", "--id", "usbipd-win", "-q"],
            ct
        );

        if (stdout != null)
            await processRunner.WriteOutputLineAsync(responseStream, stdout, false);
        if (stderr != null)
            await processRunner.WriteOutputLineAsync(responseStream, stderr, code != 0);

        if (code != 0)
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✗ winget installation failed. Please install usbipd-win manually from: https://github.com/dorssel/usbipd-win/releases",
                IsError = true,
                ExitCode = code,
            });
            logger.LogWarning("winget installation of usbipd-win failed with exit code {Code}", code);
        }
        else
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✓ usbipd-win installed successfully",
                IsError = false,
                ExitCode = 0,
            });
        }
    }

    public async Task RunWslInstallationAsync(
        IServerStreamWriter<SetupOutputEvent> responseStream,
        CancellationToken ct
    )
    {
        await responseStream.WriteAsync(new SetupOutputEvent
        {
            OutputLine = "Note: WSL installation requires elevated privileges and may require a system restart.",
            IsError = false,
            ExitCode = 0,
        });

        var (code, stdout, stderr) = await processRunner.RunProcessAsync("wsl", ["--install"], ct);

        if (stdout != null)
            await processRunner.WriteOutputLineAsync(responseStream, stdout, false);
        if (stderr != null)
            await processRunner.WriteOutputLineAsync(responseStream, stderr, code != 0);

        if (code != 0)
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✗ WSL installation may have failed. Please restart your system and try again.",
                IsError = true,
                ExitCode = code,
            });
            logger.LogWarning("WSL installation failed with exit code {Code}", code);
        }
        else
        {
            await responseStream.WriteAsync(new SetupOutputEvent
            {
                OutputLine = "✓ WSL installed. You may need to restart your system.",
                IsError = false,
                ExitCode = 0,
            });
        }
    }
}
