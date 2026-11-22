# Architecture Decision Records

**Status:** Phase 2–3 decision index  
**Last updated:** 2026-11-12

## Purpose

ADRs record decisions that affect multiple components, installation modes, data ownership, enforcement, browser integration, or future module growth. A decision is not “accepted” merely because it appears in an architecture diagram.

## Status values

- **Proposed:** recommended, awaiting user approval or technical validation.
- **Accepted:** approved and binding.
- **Accepted with conditions:** the decision is approved, but the listed conditions must be verified before implementation is considered validated.
- **Superseded:** replaced by a later ADR.
- **Rejected:** considered and not selected.

## Index

| ADR | Title | Status | Conditions / notes |
|---|---|---|---|
| [ADR-0001](ADR-0001-local-first.md) | Local-first architecture with no mandatory cloud | Accepted | None. |
| [ADR-0002](ADR-0002-agent-service-split.md) | Per-user agent owns data and enforcement UI; Windows service supervises | Proposed | Supervision loop verified in user mode (task launch, heartbeat, rate-limited restart, degraded state); session-0 service execution remains blocked on an elevated runbook. |
| [ADR-0003](ADR-0003-best-effort-enforcement.md) | Best-effort enforcement without kernel driver or anti-tamper claims | Accepted | None. |
| [ADR-0004](ADR-0004-chromium-mv3.md) | Chrome/Edge Manifest V3 with declarativeNetRequest | Proposed | MV3, fixed ID, DNR, and Native Messaging verified in Chrome/Edge 157; store ID-stability workflow documented; store accounts/review remain external. |
| [ADR-0005](ADR-0005-sqlite-single-writer.md) | SQLite with EF Core and a single-writer agent | Accepted with conditions | Agent is sole owner and only process that opens the database; SQLite WAL, snapshot, backup, and crash recovery verified at engine level; **EF Core 10 migrations, handshake, tamper detection, transactions, backup/restore, interrupted migration, and v1→v2 upgrade verified in the closure round; the migration lock was found not crash-safe (R-P3-16) — product must clear stale locks before migrating**. |
| [ADR-0006](ADR-0006-event-and-ledger.md) | Append-only session events and reward ledger | Accepted with conditions | Unique-key idempotency, atomic rollback, and concurrent duplicate prevention verified at SQLite and EF Core transaction level; domain transaction boundaries, daily cap, and `ECONOMY-1` tests remain open. |
| [ADR-0007](ADR-0007-installation-modes.md) | Full and Limited installation modes with shared data | Accepted | Limited mode verified; **closure round: elevated Full-mode install, service registration/removal, uninstall with data preservation verified**; Wow6432Node redirection requires 64-bit installer mode; upgrade/repair/rollback and real-service lifecycle remain in the elevated runbook. |
| [ADR-0008](ADR-0008-visual-authority.md) | Supplied visual references are authoritative | Accepted | All 50 UI reference gaps remain `BLOCKED — AWAITING USER REFERENCES`. |
| [ADR-0009](ADR-0009-mvp-reward-scope.md) | MVP progression is XP, levels, streak, and achievements only | Accepted | No token earning, balance, or spending in MVP; schema reserves `TOKEN`. |
| [ADR-0010](ADR-0010-provisional-packaging.md) | Provisional EXE/MSI packaging direction | Proposed | Inno Setup 7.x recommended with 64-bit install mode; Limited mode verified; **closure round: Full-mode elevated lifecycle verified with the 32-bit registry-redirection finding**; upgrade/repair/rollback and signing remain open. |

## Review rule

Before implementation begins:

1. The user reviews all `Proposed` ADRs and the conditions on `Accepted with conditions` ADRs.
2. Each is marked Accepted, revised, or rejected.
3. `system-architecture.md` and dependent requirements are updated if a decision changes.
4. The implementation authorization records which ADRs are accepted and which conditions remain open.
5. Documentation updates do not count as Windows technical validation. ADR-0002, ADR-0004, and ADR-0010 remain Proposed until their remaining Full-mode, store-policy, and integration proofs pass.
