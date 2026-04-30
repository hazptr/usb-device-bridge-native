param(
    [switch]$SkipRestore
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error 'dotnet SDK not found. Install .NET 8 SDK: https://aka.ms/dotnet/download'
}

Push-Location $PSScriptRoot\..
try {
    dotnet --info | Out-Host

    $solutionPath = $null
    if (Test-Path .\UsbDeviceBridgeNative.sln) {
        $solutionPath = '.\UsbDeviceBridgeNative.sln'
    } elseif (Test-Path .\UsbDeviceBridgeNative.slnx) {
        $solutionPath = '.\UsbDeviceBridgeNative.slnx'
    }

    if (-not $solutionPath) {
        dotnet new sln -n UsbDeviceBridgeNative
        if (Test-Path .\UsbDeviceBridgeNative.sln) {
            $solutionPath = '.\UsbDeviceBridgeNative.sln'
        } elseif (Test-Path .\UsbDeviceBridgeNative.slnx) {
            $solutionPath = '.\UsbDeviceBridgeNative.slnx'
        } else {
            throw 'Unable to locate created solution file (.sln or .slnx).'
        }
    }

    if (-not (Test-Path .\src\UsbDeviceBridge.Protos\UsbDeviceBridge.Protos.csproj)) {
        dotnet new classlib -n UsbDeviceBridge.Protos -o .\src\UsbDeviceBridge.Protos
    }

    if (-not (Test-Path .\src\UsbDeviceBridge.Service\UsbDeviceBridge.Service.csproj)) {
        dotnet new worker -n UsbDeviceBridge.Service -o .\src\UsbDeviceBridge.Service
    }

    if (-not (Test-Path .\src\UsbDeviceBridge.App\UsbDeviceBridge.App.csproj)) {
        dotnet new console -n UsbDeviceBridge.App -o .\src\UsbDeviceBridge.App
    }

    if (-not (Test-Path .\src\UsbDeviceBridge.Tests\UsbDeviceBridge.Tests.csproj)) {
        dotnet new xunit -n UsbDeviceBridge.Tests -o .\src\UsbDeviceBridge.Tests
    }

    dotnet sln $solutionPath add .\src\UsbDeviceBridge.Protos\UsbDeviceBridge.Protos.csproj
    dotnet sln $solutionPath add .\src\UsbDeviceBridge.Service\UsbDeviceBridge.Service.csproj
    dotnet sln $solutionPath add .\src\UsbDeviceBridge.App\UsbDeviceBridge.App.csproj
    dotnet sln $solutionPath add .\src\UsbDeviceBridge.Tests\UsbDeviceBridge.Tests.csproj

    if (-not $SkipRestore) {
        dotnet restore $solutionPath
    }

    Write-Host 'Native scaffold ready.' -ForegroundColor Green
}
finally {
    Pop-Location
}
