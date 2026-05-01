#!/usr/bin/env pwsh
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [string]$Runtime = 'win-x64',

    [switch]$SkipPublish,

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')

# Compute version from git describe (strips 'v' prefix; falls back to 0.0.0-dev-<hash>).
$v = & "$PSScriptRoot/Get-Version.ps1"
Write-Host "Version: $($v.AssemblyVersion)  Informational: $($v.InformationalVersion)" -ForegroundColor DarkCyan
$versionProps = @(
    "-p:Version=$($v.AssemblyVersion)"
    "-p:InformationalVersion=$($v.InformationalVersion)"
)
$wixVersionProp = "-p:PackageVersion=$($v.PackageVersion)"
$appProject = Join-Path $repoRoot 'src\UsbDeviceBridge.App\UsbDeviceBridge.App.csproj'
$serviceProject = Join-Path $repoRoot 'src\UsbDeviceBridge.Service\UsbDeviceBridge.Service.csproj'
$installerProject = Join-Path $repoRoot 'src\UsbDeviceBridge.Installer\UsbDeviceBridge.Installer.wixproj'
$servicePublishDir = Join-Path $repoRoot "src\UsbDeviceBridge.Service\bin\$Configuration\net10.0\$Runtime\publish"
$appPublishDir = Join-Path $repoRoot "src\UsbDeviceBridge.App\bin\$Configuration\net10.0-windows\$Runtime\publish"

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }

Push-Location $repoRoot
try {
    if (-not $SkipPublish) {
        Write-Host 'Publishing UsbDeviceBridge.App...' -ForegroundColor Cyan
        dotnet publish $appProject -c $Configuration -r $Runtime -p:SelfContained=$selfContainedValue @versionProps

        Write-Host 'Publishing UsbDeviceBridge.Service...' -ForegroundColor Cyan
        dotnet publish $serviceProject -c $Configuration -r $Runtime -p:SelfContained=$selfContainedValue @versionProps
    }

    Write-Host 'Building installer MSI...' -ForegroundColor Cyan
    dotnet build $installerProject -c $Configuration `
        -p:InstallerConfiguration=$Configuration `
        -p:InstallerRuntime=$Runtime `
        -p:ServicePublishDir="$servicePublishDir" `
        -p:AppPublishDir="$appPublishDir" `
        $wixVersionProp

    $msiPath = Join-Path $repoRoot "src\UsbDeviceBridge.Installer\bin\x64\$Configuration\UsbDeviceBridgeSetup.msi"
    Write-Host "Installer build complete: $msiPath" -ForegroundColor Green
}
finally {
    Pop-Location
}
