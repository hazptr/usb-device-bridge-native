#!/usr/bin/env pwsh
# Dev helper for USB Device Bridge native service and test client.
#
# USAGE
#   ./scripts/dev.ps1 service            Start the gRPC service (triggers UAC, blocking)
#   ./scripts/dev.ps1 app                Start only the app (non-elevated, service must be running)
#   ./scripts/dev.ps1 both               Start service (elevated) and app (non-elevated)
#   ./scripts/dev.ps1 <client-command>   Run a client command (no elevation needed)
#
# CLIENT COMMANDS
#   devices (d)                    List all USB devices
#   distros                        List WSL distros
#   attach (a) <bus-id> <distro>   Attach device to WSL distro
#     --remember <instance-id>     Also persist for auto-attach
#   detach (x) <bus-id>            Detach device from WSL
#   remember (r) <instance-id> <distro>  Remember device for auto-attach
#   forget (f) <instance-id>       Remove remembered device
#   remembered (rm)                List remembered devices
#   stream (s)                     Stream device change events (Ctrl+C to stop)
#
# QUICK START (two terminals)
#   Terminal 1:  ./scripts/dev.ps1 service    <- UAC prompt, then service window opens
#   Terminal 2:  ./scripts/dev.ps1 devices    <- no elevation needed
#
# QUICK START (one terminal)
#   ./scripts/dev.ps1 both                     <- Ctrl+C once graceful, twice force-stop

param(
    [Parameter(Position = 0)]
    [string]$Command = "help",

    [Parameter(ValueFromRemainingArguments)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$normalizedCommand = $Command.ToLowerInvariant()

# Compute version from git describe so dev builds stamp the right informational version.
$_v = & "$PSScriptRoot/Get-Version.ps1"
$_versionRunArgs = @(
    "--property:Version=$($_v.AssemblyVersion)"
    "--property:InformationalVersion=$($_v.InformationalVersion)"
)

# Shared shutdown-flag path used by 'both' (non-elevated) to signal 'service' (elevated).
# File-system writes are not blocked by UIPI, making this safe across integrity levels.
$script:shutdownFlag = [IO.Path]::Combine($env:TEMP, 'usbbridge-dev-shutdown.flag')

function Test-Elevated {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    ([Security.Principal.WindowsPrincipal]$id).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Stop-ProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [int]$ProcessId,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    if ($ProcessId -le 0) {
        return
    }

    if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
        return
    }

    Write-Host "Stopping $Name (PID $ProcessId)..." -ForegroundColor DarkYellow
    & taskkill /PID $ProcessId /T /F | Out-Null
}

# Walk the process tree rooted at $LauncherPid (BFS) and return the first
# descendant whose process name matches $NameGlob (e.g. 'UsbDeviceBridge*').
# Falls back to the launcher itself if no matching descendant is found.
function Get-RealProcess {
    param(
        [Parameter(Mandatory = $true)]
        [int]$LauncherPid,

        [Parameter(Mandatory = $true)]
        [string]$NameGlob
    )

    $snapshot = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue
    if (-not $snapshot) {
        return Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue
    }

    $queue = [System.Collections.Generic.Queue[int]]::new()
    $queue.Enqueue($LauncherPid)

    while ($queue.Count -gt 0) {
        $current = $queue.Dequeue()
        foreach ($child in ($snapshot | Where-Object ParentProcessId -EQ $current)) {
            if ($child.Name -like $NameGlob) {
                $found = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue
                if ($found) { return $found }
            }
            $queue.Enqueue($child.ProcessId)
        }
    }

    # Fallback: return the launcher itself
    return Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue
}

function Request-GracefulClose {
    param(
        [Parameter(Mandatory = $true)]
        [int]$LauncherPid,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        # Glob matched against process Name to find the real child exe.
        [string]$ChildNameGlob = 'UsbDeviceBridge*'
    )

    if ($LauncherPid -le 0) { return }

    # Target the real app/service exe, not the dotnet/pwsh launcher.
    $proc = Get-RealProcess -LauncherPid $LauncherPid -NameGlob $ChildNameGlob

    if (-not $proc) {
        Write-Host "${Name}: process already gone." -ForegroundColor DarkGray
        return
    }

    if ($proc.MainWindowHandle -ne 0) {
        $ok = $proc.CloseMainWindow()
        $verb = if ($ok) { 'WM_CLOSE sent to' } else { 'Could not send WM_CLOSE to' }
        Write-Host "$verb $Name ($($proc.Name), PID $($proc.Id))." -ForegroundColor Yellow
    } else {
        # No window — send Ctrl+Break via kernel32 to the process's console group.
        # This is safe cross-process as long as both share or have their own console.
        try {
            $sig = @'
using System;
using System.Runtime.InteropServices;
public static class ConsoleSignal {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);
}
'@
            if (-not ([System.Management.Automation.PSTypeName]'ConsoleSignal').Type) {
                Add-Type -TypeDefinition $sig
            }
            # CTRL_BREAK_EVENT = 1; pass the process's PID as the group ID.
            [ConsoleSignal]::GenerateConsoleCtrlEvent(1, [uint32]$proc.Id) | Out-Null
            Write-Host "Sent CTRL_BREAK to $Name ($($proc.Name), PID $($proc.Id))." -ForegroundColor Yellow
        } catch {
            Write-Host "Could not send signal to $Name; it may already be exiting." -ForegroundColor DarkGray
        }
    }
}

if ($normalizedCommand -eq 'service') {
    if (-not (Test-Elevated)) {
        # Re-launch this script elevated. UAC prompt will appear.
        # The service runs in the new elevated window; this window can be closed.
        Write-Host "Requesting admin elevation for the service..." -ForegroundColor Yellow
        $scriptPath = $MyInvocation.MyCommand.Path
        Start-Process pwsh -Verb RunAs -ArgumentList @(
            '-File', "`"$scriptPath`"",
            'service'
        )
        Write-Host "Service window opened (elevated). Run client commands in this terminal." -ForegroundColor Green
        return
    }

    # Clear any stale shutdown flag from a previous run.
    Remove-Item $script:shutdownFlag -ErrorAction SilentlyContinue

    Write-Host "[ADMIN] Starting UsbDeviceBridge.Service on http://127.0.0.1:5205 ..." -ForegroundColor Cyan
    $svcProc = Start-Process dotnet -PassThru -WorkingDirectory "$root" `
        -ArgumentList (@('run', '--project', "$root/src/UsbDeviceBridge.Service") + $_versionRunArgs)

    Write-Host "Service running (PID $($svcProc.Id)). Close this window to force stop." -ForegroundColor DarkGray

    # Poll for graceful-shutdown flag written by the non-elevated 'both' watcher.
    while (-not $svcProc.HasExited) {
        if (Test-Path $script:shutdownFlag) {
            Remove-Item $script:shutdownFlag -ErrorAction SilentlyContinue
            Write-Host "Shutdown signal received. Stopping service..." -ForegroundColor Yellow
            & taskkill /PID $svcProc.Id /T /F 2>$null | Out-Null
            break
        }
        Start-Sleep -Milliseconds 300
    }

    Write-Host "Service stopped." -ForegroundColor Green
    return
}

if ($normalizedCommand -eq 'both' -or $normalizedCommand -eq 'proto') {
    if (Test-Elevated) {
        throw "Run 'both' from a non-elevated terminal so the app remains non-admin."
    }

    $serviceProc = $null
    $appProc = $null
    $ctrlCCount = 0

    # Intercept Ctrl+C as a regular key press so PipelineStoppedException never fires
    # and we can implement two-stage shutdown ourselves.
    [Console]::TreatControlCAsInput = $true

    try {
        Write-Host "Launching elevated service in a new window..." -ForegroundColor Yellow
        $scriptPath = $MyInvocation.MyCommand.Path
        $serviceProc = Start-Process pwsh -Verb RunAs -PassThru -ArgumentList @(
            '-File', "`"$scriptPath`"",
            'service'
        )

        Write-Host "Launching UsbDeviceBridge.App..." -ForegroundColor Cyan
        $appArgs = @('run', '--project', "$root/src/UsbDeviceBridge.App") + $_versionRunArgs
        if ($Rest.Count -gt 0) {
            $appArgs += @('--') + $Rest
        }
        $appProc = Start-Process dotnet -PassThru -WorkingDirectory "$root" -ArgumentList $appArgs

        Write-Host "Press Ctrl+C once for graceful shutdown, twice to force-stop both." -ForegroundColor DarkGray

        while ($true) {
            # App exited on its own — shut down service via the flag, wait 5s, then
            # close the pwsh host window as a hard fallback.
            if (-not (Get-Process -Id $appProc.Id -ErrorAction SilentlyContinue)) {
                Write-Host "App exited. Signalling service to shut down..." -ForegroundColor Yellow
                New-Item -Path $script:shutdownFlag -ItemType File -Force | Out-Null
                $deadline = [DateTime]::UtcNow.AddSeconds(5)
                while ([DateTime]::UtcNow -lt $deadline) {
                    if (-not (Get-Process -Id $serviceProc.Id -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 200
                }
                # Service host still alive after grace period — close its window.
                if (Get-Process -Id $serviceProc.Id -ErrorAction SilentlyContinue) {
                    Write-Host "Service did not stop in time; closing its host window." -ForegroundColor DarkYellow
                    $serviceProc.CloseMainWindow() | Out-Null
                }
                break
            }

            # Poll for a Ctrl+C key press (TreatControlCAsInput makes this safe).
            if ([Console]::KeyAvailable) {
                $key = [Console]::ReadKey($true)
                if ($key.Key -eq [ConsoleKey]::C -and ($key.Modifiers -band [ConsoleModifiers]::Control)) {
                    $ctrlCCount++

                    if ($ctrlCCount -eq 1) {
                        Write-Host "Ctrl+C: requesting graceful shutdown..." -ForegroundColor Yellow
                        # App: WM_CLOSE to the WPF window (same elevation — works fine).
                        Request-GracefulClose -LauncherPid $appProc.Id -Name 'UsbDeviceBridge.App' -ChildNameGlob 'UsbDeviceBridge.App*'
                        # If tray mode consumes close and only minimizes, escalate for app.
                        $appDeadline = [DateTime]::UtcNow.AddSeconds(2)
                        while ([DateTime]::UtcNow -lt $appDeadline) {
                            if (-not (Get-Process -Id $appProc.Id -ErrorAction SilentlyContinue)) { break }
                            Start-Sleep -Milliseconds 150
                        }
                        if (Get-Process -Id $appProc.Id -ErrorAction SilentlyContinue) {
                            Write-Host "App stayed running after graceful close (likely tray minimize). Forcing app shutdown..." -ForegroundColor DarkYellow
                            Stop-ProcessTree -ProcessId $appProc.Id -Name 'UsbDeviceBridge.App'
                        }
                        # Service: write shutdown flag file — the only cross-elevation signal
                        # not blocked by UIPI. The elevated pwsh polling loop reads it.
                        New-Item -Path $script:shutdownFlag -ItemType File -Force | Out-Null
                        Write-Host "Shutdown flag written for elevated service." -ForegroundColor Yellow
                        Write-Host "Press Ctrl+C again to force terminate." -ForegroundColor Yellow
                    } else {
                        Write-Host "Force terminating both process trees..." -ForegroundColor Red
                        Remove-Item $script:shutdownFlag -ErrorAction SilentlyContinue
                        Stop-ProcessTree -ProcessId $appProc.Id -Name 'UsbDeviceBridge.App'
                        # Service is elevated; taskkill /F is denied cross-elevation.
                        # Closing the pwsh host window (WM_CLOSE via HWND) terminates
                        # the whole elevated tree. WM_CLOSE is on the UIPI allowlist.
                        if (Get-Process -Id $serviceProc.Id -ErrorAction SilentlyContinue) {
                            $serviceProc.CloseMainWindow() | Out-Null
                            Write-Host "Sent WM_CLOSE to elevated service window (PID $($serviceProc.Id))." -ForegroundColor DarkYellow
                        }
                        break
                    }
                }
            }

            Start-Sleep -Milliseconds 150
        }
    }
    finally {
        [Console]::TreatControlCAsInput = $false
    }
    return
}

if ($normalizedCommand -eq 'app') {
    if (Test-Elevated) {
        throw "Run 'app' from a non-elevated terminal so the app remains non-admin."
    }

    Write-Host "Launching UsbDeviceBridge.App..." -ForegroundColor Cyan
    $appArgs = @('run', '--project', "$root/src/UsbDeviceBridge.App") + $_versionRunArgs
    if ($Rest.Count -gt 0) {
        $appArgs += @('--') + $Rest
    }
    dotnet @appArgs
    return
}

if ($normalizedCommand -eq 'help' -or $normalizedCommand -eq '--help' -or $normalizedCommand -eq '-h') {
    Get-Content $MyInvocation.MyCommand.Path | Select-Object -First 28
    return
}

# Client commands — no elevation needed, they talk to the service over gRPC.
$clientArgs = @($Command) + $Rest
dotnet run --project "$root/src/UsbDeviceBridge.App" -- @clientArgs
