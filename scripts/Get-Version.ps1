#!/usr/bin/env pwsh
# Get-Version.ps1
# Computes build version from `git describe --tags --long --dirty`.
#
# Outputs a PSCustomObject with:
#   AssemblyVersion      — "Major.Minor.Patch"             (numeric; safe for -p:Version=)
#   InformationalVersion — full describe string minus "v"  (safe for -p:InformationalVersion=)
#   PackageVersion       — "Major.Minor.Patch"             (numeric; safe for WiX MSI version)
#
# Usage (from another script):
#   $v = & "$PSScriptRoot/Get-Version.ps1"
#   dotnet build ... -p:Version=$v.AssemblyVersion -p:InformationalVersion=$v.InformationalVersion
#
# Usage (from CI YAML):
#   $v = & ./scripts/Get-Version.ps1
#   "VERSION=$($v.AssemblyVersion)" >> $env:GITHUB_OUTPUT

$ErrorActionPreference = 'Stop'

function Get-GitVersion {
    # --long always appends commit count + hash even on exact tags.
    # --dirty appends "-dirty" when there are uncommitted changes.
    $raw = git describe --tags --long --dirty --match 'v*' 2>$null
    $gitOk = $LASTEXITCODE -eq 0

    if (-not $gitOk -or [string]::IsNullOrWhiteSpace($raw)) {
        # No tags found — use 0.0.0-dev-<hash> fallback.
        $hash = (git rev-parse --short HEAD 2>$null) ?? 'unknown'
        $gitStatus = git status --porcelain 2>$null
        $dirtySuffix = if ($gitStatus) { '-dirty' } else { '' }
        $informational = "0.0.0-dev-g${hash}${dirtySuffix}"
        return [PSCustomObject]@{
            AssemblyVersion      = '0.0.0'
            InformationalVersion = $informational
            PackageVersion       = '0.0.0'
        }
    }

    # Strip leading "v": "v1.0.0-3-g1234abc-dirty" -> "1.0.0-3-g1234abc-dirty"
    $informational = $raw.Trim().TrimStart('v')

    # Extract "Major.Minor.Patch" from the front.
    $semver = if ($informational -match '^(\d+\.\d+\.\d+)') { $Matches[1] } else { '0.0.0' }

    return [PSCustomObject]@{
        AssemblyVersion      = $semver
        InformationalVersion = $informational
        PackageVersion       = $semver
    }
}

Get-GitVersion
