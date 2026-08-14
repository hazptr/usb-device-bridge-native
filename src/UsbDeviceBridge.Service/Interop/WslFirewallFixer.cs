using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace UsbDeviceBridge.Service.Interop;

/// <summary>
/// Applies the WSL firewall fix so that usbipd TCP 3240 traffic from a WSL distro
/// is not blocked by Windows Firewall.
/// Two things are applied, both idempotent:
/// <list type="bullet">
/// <item>an inbound allow rule for TCP 3240 (usbipd's port), and</item>
/// <item>each WSL vEthernet adapter is added to the Public profile's
/// <c>DisabledInterfaceAliases</c> list.</item>
/// </list>
/// Neither step is trusted on exit code alone: the fix is verified against the
/// effective (<c>ActiveStore</c>) policy, because on group-policy-managed machines
/// local firewall settings are silently discarded and the fix becomes a no-op.
/// </summary>
public static class WslFirewallFixer
{
    /// <summary>Classification of a firewall-fix attempt.</summary>
    public enum FixStatus
    {
        /// <summary>Fix is present in the effective policy.</summary>
        Ok,

        /// <summary>Group policy discards local firewall settings; no local fix can work.</summary>
        BlockedByPolicy,

        /// <summary>Settings were written but are not present in the effective policy.</summary>
        NotEffective,

        /// <summary>No WSL vEthernet adapter exists to exclude.</summary>
        NoAdapter,

        /// <summary>The PowerShell command itself failed.</summary>
        Failed,
    }

    // Applies both steps, then verifies against the effective policy and reports a
    // machine-readable STATUS=/DETAIL= pair on stdout for the caller to classify.
    private const string FixScript = """
        $ErrorActionPreference='Stop'
        function Emit($status, $detail) { Write-Output "STATUS=$status"; Write-Output "DETAIL=$detail" }

        try {
            # Step 1: inbound allow rule for usbipd's port. usbipd's own rule is absent on
            # some installs, and this is the only step that helps on unmanaged machines.
            if (-not (Get-NetFirewallRule -Name 'UsbDeviceBridge-usbipd-3240' -ErrorAction SilentlyContinue)) {
                New-NetFirewallRule -Name 'UsbDeviceBridge-usbipd-3240' `
                    -DisplayName 'usbipd (USB Device Bridge) TCP 3240' `
                    -Direction Inbound -Protocol TCP -LocalPort 3240 `
                    -Action Allow -Profile Any -Enabled True | Out-Null
            }

            $adapters = @(Get-NetAdapter -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '*vEthernet*WSL*' })
            if ($adapters.Count -eq 0) {
                Emit 'no-adapter' 'No WSL vEthernet adapter is present.'
                exit 0
            }
            $names = @($adapters | ForEach-Object { $_.Name } | Sort-Object -Unique)

            # Step 2: exclude the WSL adapters from the Public profile.
            $cur = @((Get-NetFirewallProfile -Name Public).DisabledInterfaceAliases)
            foreach ($n in $names) { if ($cur -notcontains $n) { $cur += $n } }
            Set-NetFirewallProfile -Profile Public -DisabledInterfaceAliases $cur

            # Verify against the EFFECTIVE policy. ActiveStore may list an interface by
            # alias or by GUID, so accept either form.
            $active = @((Get-NetFirewallProfile -Name Public -PolicyStore ActiveStore).DisabledInterfaceAliases)
            $normalized = @($active | ForEach-Object { "$_".Trim('{','}').ToLowerInvariant() })
            $missing = @()
            foreach ($a in $adapters) {
                $guid = "$($a.InterfaceGuid)".Trim('{','}').ToLowerInvariant()
                $name = "$($a.Name)".ToLowerInvariant()
                if (($normalized -notcontains $name) -and ($normalized -notcontains $guid)) {
                    $missing += $a.Name
                }
            }

            if ($missing.Count -eq 0) {
                Emit 'ok' ("Excluded from the Public profile: " + ($names -join ', '))
                exit 0
            }

            # Written but not effective - find out whether group policy is the reason.
            $activeProfile = Get-NetFirewallProfile -Name Public -PolicyStore ActiveStore
            $mergeAllowed = "$($activeProfile.AllowLocalFirewallRules)"
            if ($mergeAllowed -eq 'False') {
                Emit 'blocked-by-policy' ("Group policy manages the Public firewall profile and does not " +
                    "merge local settings (AllowLocalFirewallRules=False), so the exclusion for " +
                    ($missing -join ', ') + " is discarded. Inbound TCP 3240 must be allowed by a domain " +
                    "firewall policy instead.")
            } else {
                Emit 'not-effective' ("The exclusion for " + ($missing -join ', ') +
                    " was written but is not present in the effective firewall policy.")
            }
            exit 0
        }
        catch {
            Emit 'failed' $_.Exception.Message
            exit 1
        }
        """;

    /// <summary>
    /// Applies the firewall fix via PowerShell and verifies it against the effective policy.
    /// Returns <c>(true, detail)</c> only when the fix is confirmed present; otherwise
    /// <c>(false, detail)</c> where <paramref name="detail"/> explains why it did not take.
    /// This must run in a process that has Administrator / SYSTEM privileges.
    /// </summary>
    public static async Task<(bool Ok, string Error)> ApplyPublicProfileFixAsync(
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var (status, detail) = await ApplyAndVerifyAsync(logger, cancellationToken);
        return (status == FixStatus.Ok, detail);
    }

    /// <summary>
    /// Applies the fix and returns the verified classification plus a user-facing detail string.
    /// </summary>
    public static async Task<(FixStatus Status, string Detail)> ApplyAndVerifyAsync(
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            logger.LogDebug("Skipping firewall fix: non-Windows platform.");
            return (FixStatus.Ok, "Not applicable on this platform.");
        }

        var psExe = FindPowerShellExe();
        if (psExe is null)
        {
            logger.LogError("Firewall fix cannot run: PowerShell executable not found.");
            return (FixStatus.Failed, "PowerShell not found.");
        }

        logger.LogInformation("Applying WSL firewall fix via PowerShell.");

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
                return (FixStatus.Failed, "Failed to start PowerShell process.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var (status, detail) = ParseOutcome(stdout);

            if (status is null)
            {
                // No STATUS line: the script did not run far enough to classify itself.
                var raw = (string.IsNullOrWhiteSpace(stderr) ? stdout : stderr).Trim();
                if (string.IsNullOrWhiteSpace(raw))
                    raw = $"PowerShell exited with code {process.ExitCode} and no output.";

                logger.LogWarning("Firewall fix produced no status (exit={ExitCode}): {Detail}",
                    process.ExitCode, raw);
                return (FixStatus.Failed, raw);
            }

            if (status == FixStatus.Ok)
                logger.LogInformation("Firewall fix verified: {Detail}", detail);
            else
                logger.LogWarning("Firewall fix did not take effect ({Status}): {Detail}", status, detail);

            return (status.Value, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Firewall fix threw an unexpected exception.");
            return (FixStatus.Failed, ex.Message);
        }
    }

    /// <summary>
    /// Parses the <c>STATUS=</c>/<c>DETAIL=</c> pair emitted by the fix script.
    /// Returns <c>(null, ...)</c> when no status line is present.
    /// </summary>
    internal static (FixStatus? Status, string Detail) ParseOutcome(string? stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout))
            return (null, string.Empty);

        FixStatus? status = null;
        var detail = string.Empty;

        foreach (var line in stdout.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("STATUS=", StringComparison.OrdinalIgnoreCase))
            {
                status = trimmed["STATUS=".Length..].Trim() switch
                {
                    "ok" => FixStatus.Ok,
                    "blocked-by-policy" => FixStatus.BlockedByPolicy,
                    "not-effective" => FixStatus.NotEffective,
                    "no-adapter" => FixStatus.NoAdapter,
                    _ => FixStatus.Failed,
                };
            }
            else if (trimmed.StartsWith("DETAIL=", StringComparison.OrdinalIgnoreCase))
            {
                detail = trimmed["DETAIL=".Length..].Trim();
            }
        }

        return (status, detail);
    }

    private static string? FindPowerShellExe()
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        var path = Path.Combine(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(path) ? path : null;
    }
}
