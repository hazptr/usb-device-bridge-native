using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace UsbDeviceBridge.Service.Interop;

/// <summary>
/// Applies the WSL vEthernet Public-profile firewall fix so that usbipd TCP 3240
/// traffic is not blocked by Windows Firewall group-policy rules.
/// The fix adds each WSL vEthernet adapter to the Public profile's
/// DisabledInterfaceAliases list, matching the Python reference implementation.
/// </summary>
public static class WslFirewallFixer
{
    // PowerShell command that excludes all WSL vEthernet adapters from the Public
    // firewall profile, preventing GPO/firewall rules from blocking TCP 3240.
    private const string FixScript =
        "$ErrorActionPreference='Stop';"
        + "$names=@(Get-NetAdapter -ErrorAction SilentlyContinue|"
        + "?{$_.Name -like '*vEthernet*WSL*'}|"
        + "%{$_.Name}|Sort-Object -Unique);"
        + "if($names.Count -eq 0){exit 0};"
        + "$prof=Get-NetFirewallProfile -Name Public;"
        + "$cur=@($prof.DisabledInterfaceAliases);"
        + "foreach($n in $names){if($cur -notcontains $n){$cur+=$n}};"
        + "Set-NetFirewallProfile -Profile Public -DisabledInterfaceAliases $cur";

    /// <summary>
    /// Applies the Public-profile firewall fix via PowerShell.
    /// Returns <c>(true, "")</c> on success or <c>(false, errorDetail)</c> on failure.
    /// This must run in a process that has Administrator / SYSTEM privileges.
    /// </summary>
    public static async Task<(bool Ok, string Error)> ApplyPublicProfileFixAsync(
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logger.LogDebug("Skipping firewall fix: non-Windows platform.");
            return (true, "");
        }

        var psExe = FindPowerShellExe();
        if (psExe is null)
        {
            logger.LogError("Firewall fix cannot run: PowerShell executable not found.");
            return (false, "PowerShell not found.");
        }

        logger.LogInformation("Applying WSL Public-profile firewall fix via PowerShell.");

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = psExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(FixScript);

            using var process = Process.Start(psi);
            if (process is null)
                return (false, "Failed to start PowerShell process.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                if (!string.IsNullOrWhiteSpace(stdout))
                    logger.LogInformation("Firewall fix succeeded with output: {Output}", stdout.Trim());
                else
                    logger.LogInformation("Firewall fix succeeded.");
                return (true, "");
            }

            var detail = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
            if (string.IsNullOrWhiteSpace(detail))
                detail = "Set-NetFirewallProfile failed.";

            logger.LogWarning("Firewall fix failed (exit={ExitCode}): {Detail}", process.ExitCode, detail);
            return (false, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Firewall fix threw an unexpected exception.");
            return (false, ex.Message);
        }
    }

    private static string? FindPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }
}
