# USB Device Bridge Native

<div align="center">
  <img src="src/UsbDeviceBridge.App/Assets/app_icon.ico" alt="USB Device Bridge Logo" width="200" />
</div>

<div align="center">

![.NET](https://img.shields.io/badge/.NET-8.0+-512BD4?style=flat-square)
![C#](https://img.shields.io/badge/C%23-Latest-239120?style=flat-square)
![Windows](https://img.shields.io/badge/Windows-10/11-0078D4?style=flat-square)
![License](https://img.shields.io/badge/License-MIT-blue?style=flat-square)

</div>

**A native C# rewrite of [USB Device Bridge for WSL](https://github.com/delabrcd/usbipd-device-attach-manager)—written by someone who doesn't know C# and just let AI write all of it.**

A Windows desktop application built with WPF and .NET that simplifies USB device sharing between Windows and Linux in WSL2. Attach USB peripherals to your Linux environment with one-click operations and automatic device reconnection when devices are plugged in.

> **Fair Warning:** This entire codebase was vibe coded—an AI wrote it all while the author watched nervously. If it works, that's surprising. If it doesn't, blame the prompt.

## Features

- **Simple USB Management:** Browse all USB devices connected to your Windows PC in a modern WPF interface
- **Automatic Reconnection:** Mark devices to remember them—USB Device Bridge automatically reattaches them to WSL2 when plugged in while the app is running
- **Per-Device Configuration:** Assign each USB device to a specific WSL distribution
- **No Command-Line Required:** Full GUI for device discovery and attachment
- **Persistent Settings:** Your device preferences survive app restarts
- **Modern Windows Integration:** Built with WPF for a native Windows desktop experience

## Architecture

- **`UsbDeviceBridge.Service`:** Elevated Windows Service (admin boundary) handling device operations
- **`UsbDeviceBridge.App`:** Non-elevated desktop UI client for user interactions
- **`UsbDeviceBridge.Protos`:** gRPC contracts shared between app and service

## Requirements

- **Windows 10/11** with Administrator rights
- **WSL2** installed and configured
- **[usbipd-win](https://github.com/dorssel/usbipd-win)** (the app guides you to install it if missing)
- **.NET SDK 8 or later**

## Quick Start

### Prerequisites Install

```powershell
pwsh ./scripts/bootstrap-native.ps1
```

### Build

```bash
dotnet build
```

### Run

```bash
dotnet run --project UsbDeviceBridge.App
```

## Development

- **Implementation plan:** [docs/implementation-plan.md](docs/implementation-plan.md)
- **USB/IP protocol research:** [docs/usbip-protocol-research.md](docs/usbip-protocol-research.md)
- **WSL interop research:** [docs/wsl-interop-research.md](docs/wsl-interop-research.md)
- **Agent requirements:** [docs/agent-requirements.md](docs/agent-requirements.md)

## License

See [LICENSE](LICENSE).
