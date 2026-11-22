# ADR-0003: Best-effort enforcement without kernel driver or anti-tamper claims

- **Status:** Accepted
- **Date:** 2026-11-12
- **Decision owner:** User
- **Related:** Product requirements, application guard, browser guardian, known limitations

## Context

Boundless Study runs on a personal computer controlled by its owner. A student can stop the service, disable the extension, use another browser, use a virtual machine, edit the local database, or use another device. No user-mode application can guarantee blocking against a determined local administrator or owner.

The product must help a cooperating student without becoming spyware, a parental-control product, or a false security claim.

## Decision

- Enforcement is best effort.
- Application blocking uses foreground detection and a visible overlay.
- The agent does not terminate, suspend, inject into, or modify blocked processes.
- Website blocking uses a browser extension and declarative rules.
- No kernel driver, global hook injection, stealth persistence, or undocumented Windows modification is used.
- Full mode adds service supervision and recovery, not tamper-proof enforcement.
- Onboarding, pre-session disclosure, Settings, release notes, and support documentation state the limitations.
- Protection status must be truthful and surface degraded/unavailable states.

## Consequences

- The product cannot promise exam integrity, parental control, or anti-cheat.
- Fullscreen-exclusive apps and other browsers may bypass overlays.
- A user can disable protection; the product records and displays the resulting state.
- Trust is preserved through transparency and reversible installation.
- The application must pause tracking when enforcement fails rather than credit blocked time.

## Alternatives considered

1. **Kernel filter driver:** Rejected for safety, complexity, signing, compatibility, and proportionality.
2. **Process termination as enforcement:** Rejected for data loss and hostile behavior.
3. **Network-level proxy or DNS filtering:** Rejected for MVP because it is invasive, system-wide, and difficult to make transparent.
4. **Silent watchdog persistence:** Rejected as hidden persistence.

## Review trigger

Any proposal to add stronger enforcement must include a threat model, user consent, safety analysis, uninstall/recovery path, and an explicit revision of the product promise.
