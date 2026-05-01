# USB Device Bridge Native

<div align="center">
  <img src="src/UsbDeviceBridge.App/Assets/app_icon.ico" alt="USB Device Bridge Logo" width="200" />
</div>

<div align="center">

![.NET](https://img.shields.io/badge/.NET-10.0+-512BD4?style=flat-square)
![C#](https://img.shields.io/badge/C%23-Latest-239120?style=flat-square)
![Windows](https://img.shields.io/badge/Windows-10/11-0078D4?style=flat-square)
![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)

</div>

**A native C# rewrite of [USB Device Bridge for WSL](https://github.com/delabrcd/usbipd-device-attach-manager)—written by someone who doesn't know C# and just let AI write all of it.**

A Windows desktop application built with WPF and .NET that simplifies USB device sharing between Windows and Linux in WSL2. Attach USB peripherals to your Linux environment with one-click operations and automatic device reconnection when devices are plugged in.

> **Fair Warning:** This entire codebase was vibe coded—an AI wrote it all while the author watched nervously. If it works, that's surprising. If it doesn't, blame the prompt.

## Features

- **Device Discovery & Live Updates:** Real-time USB device list with live streaming from the service
- **One-Click Attach / Detach:** Bind and attach any USB device to WSL2 without touching a terminal
- **Auto-Attach on Reconnect:** Remember devices—the app automatically reattaches them whenever they are plugged back in (client-driven, no elevated polling)
- **Firewall & Busy Detection:** Classifies attach failures as firewall blocks or busy-device conflicts and surfaces actionable guidance
- **Force-Retry Support:** Override busy or conflicting attach attempts directly from the UI
- **WSL Distribution Selection:** Query available WSL distros and pin each device to its own distro
- **Detach on Exit:** Optionally release all attached devices when the app closes
- **Settings Overlay:** Per-device remembered state, distro assignment, and sort-order preferences—all persisted locally
- **Settings Reset:** One-click wipe of all remembered devices and preferences
- **Service Recovery Prompt:** Detects a stopped/crashed service and offers an in-app restart action (single elevated call, no full-process elevation)
- **System Tray Integration:** Minimize to tray; balloon notifications for attach/detach events
- **Start with Windows:** Optional HKCU Run entry—app launches at login and starts the service if needed
- **Setup Flow:** Multi-step guided installer for usbipd-win and WSL2 prerequisites with live progress log
- **MSI Installer:** WiX-packaged installer that bundles the app and service together

## Architecture

```
UsbDeviceBridge.App          (non-elevated WPF desktop UI)
  ├── LocalDeviceManager     ← polls usbipd state; drives device list
  ├── LocalAutoAttachManager ← watches plug events; triggers attach RPCs
  ├── WslUserSpaceInterop    ← queries WSL distros without elevation
  └── gRPC client            → UsbDeviceBridge.Service

UsbDeviceBridge.Service      (elevated Windows Service – privileged boundary)
  ├── DeviceServiceImpl      ← bind / attach / detach RPCs
  ├── SetupServiceImpl       ← prerequisite check + install RPCs
  └── AdminServiceImpl       ← service-lifecycle helpers

UsbDeviceBridge.Protos       (shared gRPC contracts)
```

The service is a **thin privileged executor**—it runs elevated so the app never needs to be. Auto-attach logic, device state tracking, and WSL interop all live in the non-elevated app process.

## Requirements

- **Windows 10/11**
- **WSL2** installed and configured
- **[usbipd-win](https://github.com/dorssel/usbipd-win)** ≥ 4.x (the app guides you to install it if missing)
- **.NET 10 Runtime** (Desktop)

## Quick Start

### Development build & run

```powershell
# Build and launch both service and app in debug mode
.\scripts\dev.ps1 both
```

### Build only

```powershell
dotnet build
```

### Run tests

```powershell
dotnet test src/UsbDeviceBridge.Tests/UsbDeviceBridge.Tests.csproj
```

### Build installer (MSI)

```powershell
.\scripts\build-installer.ps1
```

## Development

- **Feature index:** [docs/features/README.md](docs/features/README.md)
- **Implementation plan:** [docs/implementation-plan.md](docs/implementation-plan.md)
- **USB/IP protocol research:** [docs/usbip-protocol-research.md](docs/usbip-protocol-research.md)
- **WSL interop research:** [docs/wsl-interop-research.md](docs/wsl-interop-research.md)
- **Agent requirements:** [docs/agent-requirements.md](docs/agent-requirements.md)

## License

See [LICENSE](LICENSE).
