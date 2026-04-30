# USB Device Bridge Native

Parallel native implementation track for the Windows-only rewrite.

## Scope
- Existing Python/Flet code remains untouched.
- All native rewrite code and docs live under this folder.

## Target architecture
- `UsbDeviceBridge.Service`: elevated Windows Service (admin boundary).
- `UsbDeviceBridge.App`: non-elevated desktop UI client.
- `UsbDeviceBridge.Protos`: gRPC contracts shared by app and service.

## Current status
- Repository scaffolded.
- Protocol and interop research docs added.
- Service interop skeletons added for usbipd TCP and WSL hybrid APIs.

## Documentation
- Full implementation plan: [docs/implementation-plan.md](docs/implementation-plan.md)
- USB/IP protocol research: [docs/usbip-protocol-research.md](docs/usbip-protocol-research.md)
- WSL interop research: [docs/wsl-interop-research.md](docs/wsl-interop-research.md)
- Agent requirements checklist: [docs/agent-requirements.md](docs/agent-requirements.md)

## Prerequisites
- .NET SDK 8 or later
- Windows 11/10 with WSL2 and usbipd-win installed

## Next step once SDK is available
Run:

```powershell
pwsh ./scripts/bootstrap-native.ps1
```
