# ADR-0007: Full and Limited installation modes with shared data

- **Status:** Accepted
- **Date:** 2026-11-12
- **Decision owner:** User
- **Related:** Product requirements, installer, system integration, upgrade

## Context

Students use both personal computers and school-managed devices. Some can install a Windows service; many cannot. A Full-mode-only product would exclude a large part of the target audience. A Limited-mode-only product would fail the requirement for independent Windows service supervision and stronger enforcement.

## Decision

Support two clearly differentiated installation modes:

### Full mode

- Administrator privileges during installation.
- Independent Windows service.
- Service-supervised per-user agent.
- Stronger application-enforcement reliability through documented supervision and recovery.
- Browser extension and Native Messaging.
- Transparent permissions, recovery, upgrade, and uninstall.
- One elevation prompt for installation/upgrade/repair, not repeated runtime prompts.

### Limited mode

- Installs and runs without administrator privileges.
- No Windows service.
- Supports focus sessions, tasks, subjects, tracking, XP, progression, analytics, and local data.
- Supports browser extension integration when browser and school policy permit.
- Supports browser-based website restrictions where available.
- Application enforcement is best effort and unsupervised.
- Never displays protection as active when unavailable.

#### Limited-mode application blocking (clarified 2026-11-12)

Limited mode MUST implement best-effort application blocking through the per-user agent when the supported Windows mechanisms are available. The requirement is to implement the capability and report it honestly, not to guarantee that every application can be blocked.

Supported without administrator rights:

- User-session foreground-change detection through Windows foreground events, with a bounded polling fallback.
- User-session process identity and normalized executable matching.
- An agent-owned topmost overlay in the interactive session, with keyboard-accessible return-to-study and end-session actions.
- The 3-second grace period, violation recording, and enforcement-failure recording.

Requires administrator privileges or is not available to Limited mode:

- Installing the Windows service and service-supervised agent recovery. These are Full-mode capabilities.
- Reliable detection or overlay of elevated or protected processes from a standard-integrity agent.
- Fullscreen-exclusive surfaces that suppress or obscure overlays.
- Applications in other Windows sessions or on the secure desktop.
- Kernel-level, cross-session, or tamper-proof enforcement. Neither mode uses a kernel driver or process injection.

When enforcement is unavailable:

- The session pauses; an enforcement failure is recorded; a persistent notice names the affected target and recovery action; no blocked time is credited as study.
- Targets are reported as `Supported`, `Unsupported`, or `Unverified`. `Unsupported` and `Unverified` are never shown as `Protected`.
- Limited mode reports supervision as `Best effort`, not `Supervised`.

Why Limited mode cannot promise system-wide or tamper-proof enforcement:

- The agent runs at the signed-in user's integrity level, in one interactive session, and can be stopped by that user.
- Windows provides no supported standard-user API that can guarantee foreground detection or overlay above higher-integrity or protected windows.
- Full mode adds service supervision and restart. It does not add kernel enforcement, system-wide coverage, elevated-target coverage, or an anti-tamper guarantee.

### Shared rules

- Both modes use the same local profile, data path, SQLite schema, domain logic, UI, backup format, and data ownership model.
- Shared functionality does not depend on the privileged service.
- Limited mode can upgrade to Full mode through a documented user-initiated flow.
- Upgrade preserves tasks, settings, study history, XP, achievements, any future token data if present, and profile identity.
- A failed or canceled upgrade leaves Limited mode intact.
- School-policy restrictions are surfaced gracefully.
- No hidden persistence or undocumented Windows modification is used.
- Both installation paths are tested independently.

## Consequences

- Two support and test matrices.
- Capability states must be live and truthful, not inferred from a static build label.
- Upgrade packaging and native-host path changes must be carefully designed.
- The installer must support per-user and per-machine installation components.
- Full mode cannot be described as tamper-proof.
- Limited mode is a legitimate product mode, not a broken Full mode.

## Alternatives considered

1. **Full mode only:** Rejected because it excludes students without administrator rights.
2. **Limited mode only:** Rejected because it does not meet the service/enforcement requirement.
3. **Two separate products:** Rejected because it would duplicate data, UI, and support.
4. **Repeated elevation prompts:** Rejected as hostile and unnecessary.

## Review trigger

Review after Phase 3 installer and service-supervision validation. If Full-mode supervision cannot be delivered reliably, revise Full-mode claims while keeping Limited mode functional and honest.

## Phase 3 validation evidence (2026-11-12)

- `VERIFIED` for Limited mode with an Inno Setup 6.0.5 test build: silent per-user install exited 0 to `%LocalAppData%\Programs\BoundlessStudySpike`; HKCU Chrome/Edge Native Messaging host keys and a Run value were created; no Program Files directory and no service were created.
- `VERIFIED`: upgrade to 1.1.0 preserved modified seed data, kept one uninstall entry, and updated the recorded version.
- `VERIFIED`: uninstall removed the app directory and HKCU keys and preserved `%LocalAppData%\BoundlessStudySpike\data` with an explicit marker.
- `BLOCKED`: Full-mode per-machine install, HKLM registration, service account/creation, Limited-to-Full upgrade, repair, rollback, and signing were not executed.
- `BLOCKED`: clean-VM evidence for either mode does not exist.
- Disposition: Limited-mode boundaries are technically understood; Full-mode boundaries remain unproven. Status remains Accepted.

### Closure-round evidence (2026-11-13)

- `VERIFIED` for Full mode with an Inno Setup 6.0.5 elevated run (UAC consent granted interactively): silent install exit 0 to `Program Files (x86)`; the `BoundlessStudySpikeSvc` service was registered (`WIN32_OWN_PROCESS`, `AUTO_START`, LocalSystem, correct binPath); elevated silent uninstall exit 0 removed the service (query returned 1060), the install directory, and the HKLM keys, and preserved `%LocalAppData%` user data with a marker.
- `VERIFIED` (boundary): non-elevated `sc start` and `sc delete` are denied with error 5 — service control stays privileged as designed.
- `FAILED` as configured and corrected in the record: the 32-bit compiler wrote HKLM Native Messaging keys under `WOW6432Node`. The product must use 64-bit install mode so the keys land in the native registry view (see R-P3-13).
- `NOT EXECUTED`: service start/stop with a real SCM service binary, Limited-to-Full upgrade, repair, rollback, clean-VM run.
- Disposition: the elevated Full-mode install/uninstall lifecycle is now experimentally proven at spike level; the remaining lifecycle steps have an explicit owner (user) and runbook. Status remains Accepted.
