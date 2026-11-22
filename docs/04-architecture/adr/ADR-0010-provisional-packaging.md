# ADR-0010: Provisional EXE/MSI packaging direction

- **Status:** Proposed
- **Date:** 2026-11-12
- **Decision owner:** User approval required
- **Related:** Installation modes, native messaging, Windows service, notifications

## Context

Boundless Study needs two installation paths:

- Full mode with an elevated Windows service, Native Messaging registration, per-user agent startup, and a clean uninstall.
- Limited mode installed and run by a standard user without a service.

The application is WPF on .NET 10. Browser Native Messaging requires registry registration and an executable path. Windows toast notifications for unpackaged applications require an AppUserModelID and shortcut/registration strategy. MSIX is strong for packaged desktop apps but complicates Windows service installation and some native-host/browser integration scenarios.

## Decision

Provisional direction:

- Use a signed EXE/MSI-based bootstrapper or installer with explicit Full and Limited paths.
- Full mode installs per-machine binaries and the service, plus per-user startup registration for the agent.
- Limited mode installs per-user binaries and registration under HKCU/LocalAppData.
- Both modes use the same application data path and schema.
- Upgrade is a user-initiated installation that preserves data.
- Uninstall is explicit about data retention and extension removal.
- Auto-update is not part of MVP. Any future updater must verify signatures and support rollback/recovery.
- MSIX is not assumed for Full mode unless a validated service and Native Messaging design is proven.

This is a packaging direction, not a final technology selection. The exact installer framework is a Phase 3 decision.

## Consequences

- The installer must handle UAC, service lifecycle, registry entries, startup tasks, upgrade, repair, and uninstall.
- Code signing is required for trust and SmartScreen.
- Toast notification registration must be proven separately.
- Per-user and per-machine file locations must be tested for both modes.
- Installer code is security-sensitive and needs its own test matrix.
- The user may see a standard Windows installer rather than a packaged app.

## Alternatives considered

1. **MSIX for both modes:** Not selected provisionally because Full mode’s service and Native Messaging requirements need proof.
2. **ClickOnce:** Rejected for limited service/registry control and upgrade constraints.
3. **Portable ZIP:** Rejected for service installation, upgrade, uninstall, and trust.
4. **Custom bootstrapper only:** Possible later, but not assumed.

## Review trigger

Phase 3 must produce a tested installer proof for both modes, native host registration, toast registration, upgrade, and uninstall. This ADR is updated before implementation.

## Phase 3 validation evidence (2026-11-12)

- `VERIFIED` for Limited mode with Inno Setup 6.0.5: a single EXE installed per-user, registered HKCU Native Messaging hosts and a Run value, upgraded with data preservation, and uninstalled while preserving user data.
- `BLOCKED`: Full-mode per-machine installation, service creation/removal, HKLM registration, repair, rollback, and Limited-to-Full upgrade were not executed.
- `BLOCKED`: code signing, timestamping, SmartScreen, and toast-notification registration were not tested.
- `RECOMMENDED`: Inno Setup 7.x for the MVP, with WiX/Burn as the MSI fallback and NSIS documented but not selected. The test compiler was 6.0.5, not the recommended current version.
- Disposition: packaging direction is partially validated. Status remains Proposed pending Full-mode and signing evidence.

### Closure-round evidence (2026-11-13)

- `VERIFIED`: the Full-mode script (admin privileges, HKLM registration, `sc.exe` lifecycle, user-data preservation) compiles and its elevated install/uninstall lifecycle executed successfully (service registered and removed, data preserved).
- `FAILED` as configured: the 32-bit compiler redirected HKLM writes into `WOW6432Node`; correction is to use the Inno Setup 7.x 64-bit build / `64:` registry prefixes and re-verify.
- `NOT EXECUTED`: Limited-to-Full upgrade, repair, rollback, code signing.
- Disposition: Inno Setup 7.x remains the recommendation, now with an explicit 64-bit-mode requirement. Status remains Proposed.
