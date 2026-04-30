# Native Project Agent Requirements

This file applies to work under `usb-device-bridge-native/` only.

## Hard boundaries
- Do not edit files outside `usb-device-bridge-native/` in this migration track.
- UI must run without admin privileges by default.
- Exception: UI may trigger a single elevated command/call only for service installation/configuration/lifecycle actions when strictly necessary.
- Elevation must be scoped to that one command/call; never elevate the whole app process.
- Privileged operations must stay in the Windows Service.

## Coding constraints
- C# with nullable reference types enabled.
- Avoid reflection-heavy and runtime-dynamic patterns unless strictly required.
- Keep protocol parsing deterministic and testable.
- Prefer explicit DTOs and enums over loosely typed maps.

## USB/IP and WSL interoperability requirements
- usbipd integration should use TCP protocol interaction where feasible.
- WSL integration should use native `wslapi.dll` where supported and minimal `wsl.exe` fallback for unsupported operations.

## Required validation before handoff
- Build all projects in solution (when SDK is installed).
- Run unit tests for protocol parsing.
- Verify docs updated when protocol/discovery approach changes.

## Research entry points
- See `docs/implementation-plan.md` for full implementation scope and sequencing.
- See `docs/usbip-protocol-research.md`.
- See `docs/wsl-interop-research.md`.
- See `docs/agent-requirements.md` for the full checklist.

## Session-learned codebase knowledge
- Feature specs are now split into one-file-per-feature under `docs/features/`; `docs/features/README.md` is the feature index and planning entry point for parallel agent work.
- For setup prerequisites, the intended architecture is service-driven: UI triggers setup RPCs and renders streamed logs; service decides command list and executes privileged operations.
- Settings reset is expected to be fully automated: forget remembered devices through service RPCs, clear local settings, then restart app without user intervention.
- Startup model is UI-first from HKCU Run entry; on launch, UI checks service status and, if stopped, shows a visible Start Service action that triggers elevated start flow.
- Service lifecycle controls should minimize UAC prompts: status checks are read-only/no elevation; elevation occurs only when user requests one specific privileged action.
- Delivery scope should include CI automation and installer bundling: add a GitHub Actions pipeline and an installer that installs the full app bundle (UI + service + prerequisites flow hooks).
