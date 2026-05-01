<#
.SYNOPSIS
    Generates a WiX v4 fragment (HarvestedFiles.g.wxs) by scanning two publish directories.
    Invoked automatically by the .wixproj MSBuild target before compilation.

.PARAMETER ServicePublishDir
    Path to the dotnet-publish output of UsbDeviceBridge.Service.

.PARAMETER AppPublishDir
    Path to the dotnet-publish output of UsbDeviceBridge.App.

.PARAMETER OutputFile
    Destination path for the generated .wxs file (typically under the
    project's intermediate-output directory).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServicePublishDir,
    [Parameter(Mandatory)][string]$AppPublishDir,
    [Parameter(Mandatory)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'

function ConvertTo-WixId([string]$prefix, [string]$relativePath) {
    # WiX identifiers may only contain letters, digits, underscores, and dots.
    # Replace anything else with underscores and prefix with the group prefix.
    $safe = $relativePath -replace '[^a-zA-Z0-9_.]', '_'
    return "${prefix}_${safe}"
}

function Write-ComponentGroup {
    param(
        [System.Text.StringBuilder]$sb,
        [string]$groupId,
        [string]$directoryRef,
        [string]$rootDir,
        [string[]]$excludeNames,
        [string[]]$excludeExtensions
    )

    if (-not (Test-Path $rootDir)) {
        Write-Error "Publish directory not found: $rootDir`nRun 'dotnet publish' for the relevant project first."
    }

    # Resolve to canonical absolute path so that Substring length arithmetic is correct
    # even when the input contains '\..\' segments.
    $rootDir = [System.IO.Path]::GetFullPath($rootDir).TrimEnd('\', '/')
    [void]$sb.AppendLine("  <Fragment>")
    [void]$sb.AppendLine("    <ComponentGroup Id=""$groupId"" Directory=""$directoryRef"">")

    $files = Get-ChildItem -Path $rootDir -File -Recurse | Where-Object {
        $excludeNames      -notcontains $_.Name -and
        $excludeExtensions -notcontains $_.Extension.ToLowerInvariant()
    }

    foreach ($file in $files) {
        $rel     = $file.FullName.Substring($rootDir.Length + 1)
        $compId  = ConvertTo-WixId -prefix $directoryRef -relativePath $rel
        $fileId  = "F_$compId"

        [void]$sb.AppendLine("      <Component Id=""$compId"" Guid=""*"">")
        [void]$sb.AppendLine("        <File Id=""$fileId"" Source=""$($file.FullName)"" KeyPath=""yes"" />")
        [void]$sb.AppendLine("      </Component>")
    }

    [void]$sb.AppendLine("    </ComponentGroup>")
    [void]$sb.AppendLine("  </Fragment>")
}

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine('<?xml version="1.0" encoding="utf-8"?>')
[void]$sb.AppendLine('<!-- AUTO-GENERATED — do not edit.  Regenerated before every build. -->')
[void]$sb.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$sb.AppendLine()

# Service files: exclude the main exe (handled by ServiceExeComponent) and .pdb symbols.
Write-ComponentGroup `
    -sb              $sb `
    -groupId         'ServiceHarvestedFiles' `
    -directoryRef    'SERVICEDIR' `
    -rootDir         $ServicePublishDir `
    -excludeNames    @('UsbDeviceBridge.Service.exe') `
    -excludeExtensions @('.pdb')

[void]$sb.AppendLine()

# App files: exclude .pdb symbols.
Write-ComponentGroup `
    -sb              $sb `
    -groupId         'AppHarvestedFiles' `
    -directoryRef    'APPDIR' `
    -rootDir         $AppPublishDir `
    -excludeNames    @('UsbDeviceBridge.App.exe') `
    -excludeExtensions @('.pdb')

[void]$sb.AppendLine('</Wix>')

$outDir = Split-Path $OutputFile -Parent
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

[System.IO.File]::WriteAllText($OutputFile, $sb.ToString(), [System.Text.Encoding]::UTF8)
Write-Host "Harvested files written to: $OutputFile"
