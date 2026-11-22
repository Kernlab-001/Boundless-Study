# Development Roadmap

**Product:** Boundless Study  
**Status:** Draft for user review  
**Version:** 0.1  
**Last updated:** 2026-11-12  
**Related:** `product-requirements.md`, `../04-architecture/system-architecture.md`, `../05-quality/test-strategy.md`

## 1. Roadmap principles

- Deliver one complete user loop before expanding the feature surface.
- Build the shared core so Full and Limited modes use the same domain, UI, and data.
- Treat the Windows service as optional supervision, not a dependency of study features.
- Prove installation, recovery, and uninstall early. They are product features, not release chores.
- Do not mark a milestone complete because code exists. A milestone closes only when its exit criteria are evidenced.
- No implementation begins until the user explicitly authorizes development after Phase 2 review.

## 2. Phase overview

| Phase | Outcome | Implementation allowed? |
|---|---|---|
| Phase 2 — Detailed Requirements | SRS, requirements, domain rules, UX flows, data model, architecture, ADRs, and test strategy submitted for user approval. | No |
| Phase 3 - Architecture and Technical Validation | Technical spikes, installer feasibility, service/agent restart proof, extension proof, and reference inventory only. No visual design completion. | Executed; conditionally complete. Phase 4 still requires authorization. |
| Phase 4 - MVP Foundation | Installable shell, local data store, agent, Full/Limited modes, onboarding diagnostics, and a basic functional UI for operation and testing. | Requires authorization |
| Phase 5 - MVP Core Loop | Tasks, focus sessions, tracking, app guard, browser guard, recovery, and functional interfaces for each feature. | Requires authorization |
| Phase 6 - MVP Motivation and Review | XP, levels, streak, achievements, insights, notifications, and functional interfaces for each feature. | Requires authorization |
| Phase 7 - Dedicated UI Phase | UI specification and reference-conformant visual implementation after sufficient functional integration. | Requires authorization |
| Phase 8 - Hardening and Release | Final visual acceptance, security, accessibility, performance, destructive testing, packaging, signing, documentation, and release stabilization. | Requires authorization |
| Release 2 — Motivation Expansion | Companion, challenges, token shop, themes. | Separate scope approval |
| Release 3 — Planning and Intelligence | Smart planner, scheduled profiles, advanced analytics. | Separate scope approval |

## 3. Phase 3 — Architecture and Technical Validation

### Purpose

Resolve the highest-risk technical assumptions before product code is committed. These are bounded feasibility activities, not the start of the application. Functional UX may be reviewed for feasibility, but Phase 3 does not require completed visual design.

### Required validations

1. **Full/Limited installer proof.** Install and uninstall both modes on clean Windows 11 VMs, with and without administrator rights.
2. **Service supervision proof.** Determine how the service starts, monitors, and restarts the per-user agent without hidden persistence. Test logon, logoff, fast user switching, sleep, and reboot.
3. **Named-pipe security proof.** Verify user-scoped ACLs, client identity checks, protocol framing, malformed-message handling, and reconnect behavior.
4. **Native Messaging proof.** Verify Chrome and Edge registration in HKCU, fixed extension ID, host launch, policy push, heartbeat, and browser-policy failure states.
5. **Application-guard proof.** Verify foreground detection, grace period, overlay behavior, multi-monitor, fullscreen limitations, and no false “protected” states.
6. **SQLite proof.** Verify WAL mode, agent-only database access, EF Core migrations, online backup, restore, integrity check, and corruption recovery.
7. **Session timing proof.** Verify monotonic active time, lock/sleep interruption, clock changes, checkpoint recovery, and no duplicate rewards.
8. **Visual reference intake only.** Inventory supplied references and assets and record them in the gap log. Do not design, implement, or invent a visual style. This is intake; completed visual design is not a Phase 3 exit criterion.

### Exit criteria

- A written feasibility report for each item.
- The service/agent approach is accepted, revised, or rejected.
- Installer technology is selected.
- Browser distribution constraints are documented.
- Visual reference inventory is recorded; visual design completion is not a Phase 3 exit criterion.
- The architecture and ADRs are updated before implementation.
- The user authorizes the next phase.

### Execution status (2026-11-13)

Phase 3 has been executed to the extent the environment permits, including the closure round. Browser MV3, fixed extension ID, DNR, Native Messaging, SQLite WAL/backup/crash recovery, ledger idempotency, named-pipe framing/concurrency/reconnect, session isolation, and Limited-mode installer install/upgrade/uninstall have reproducible evidence. The closure round added: the Full-mode supervision loop verified in user mode (task launch, heartbeat, crash restart, adoption, rate-limited degraded state), the elevated Full-mode installer install/uninstall with service registration/removal and data preservation (with a Wow6432Node correction required), EF Core 10 migrations/handshake/transactions/backup/restore/interrupted-migration/schema-upgrade evidence on a per-user .NET 10 SDK, documented store publishing requirements, and PoC-level guard detection. The installer is provisionally recommended as Inno Setup 7.x with 64-bit install mode but is not finally selected. Remaining Full-mode service/session behaviors, Limited-to-Full upgrade, repair, rollback, and signing have an explicit owner and runbook. See `../07-phase-3/phase-3-validation-record.md`. Phase 4 remains unauthorized.

## 4. Phase 4 — MVP Foundation

Phases 4–6 are functionality-first. They build working business logic, application services, persistence, the focus engine, application and browser enforcement; XP, achievements, and progression; and IPC and Windows integration. A basic functional UI may be used to operate and test each feature. It is a temporary interface, not final visual design, and it does not require visual approval.

### Outcomes

- Signed development build and installer shells for Full and Limited modes.
- Application shell with functional navigation and a basic functional UI sufficient to operate and test the foundation; no invented visual styling.
- Per-user agent owns the database and session state.
- Full-mode service can be installed and removed without affecting user data.
- Onboarding detects permissions, existing data, browsers, extension status, and mode availability.
- Mode selection and one-time Limited-to-Full upgrade path are functional.
- Local logging, diagnostics, schema versioning, and backup foundations exist.

### Exit criteria

- Clean install and uninstall in both modes.
- Limited mode never triggers an elevation prompt except when the user explicitly chooses the upgrade path.
- Upgrade preserves a seeded test dataset exactly.
- Agent is the sole database owner and the only process that opens SQLite, for reads and writes.
- No visual screen is marked visually approved without a reference.
- Crash and restart do not corrupt the database.

## 5. Phase 5 — MVP Core Loop

### Outcomes

- Subjects and tasks with due dates, estimates, priority, status, and today view.
- Flexible, Balanced, and Strict focus sessions, breaks, early exit, system interruption, and recovery.
- Application guard with user-selected blocked applications, overlay, violations, and explicit limitations.
- Chrome/Edge website rules, allow rules, heartbeat, degraded states, and policy cleanup.
- Session tracking with elapsed, active, idle, break, violation, and protection-gap metrics.
- Session summary and history.
- First-run and recovery flows tested end to end.

### Exit criteria

- Start-to-completion loop works in Full and Limited modes.
- Blocking is never shown as active when unavailable.
- A crash during a session restores a checkpoint without crediting downtime.
- A blocked site and blocked app each produce the specified UX.
- Early exit and emergency exceptions obey profile rules.
- Browser close, extension disable, bridge disconnect, and service stop produce correct states.
- No forced process termination or hidden persistence.

## 6. Phase 6 — MVP Motivation and Review

### Outcomes

- XP ledger with idempotent awards, daily cap, level curve, and level-up feedback.
- Streak calculation based on qualified days.
- Five MVP achievements and achievement feedback.
- Today, this week, and last 30 days insights.
- Subject split and session history with accessible alternatives.
- Minimal notifications for session end, break end, protection loss, achievements, and backup failure.
- Token ledger schema is forward-compatible, but no token balance, token earning, or shop is exposed.

### Exit criteria

- Duplicate award attempts are rejected.
- Restart and recovery never duplicate XP.
- Task completion XP is awarded once.
- Daily cap and timezone rules are tested.
- Streak changes are deterministic.
- Insights match domain definitions and include empty/insufficient-data states.
- Achievement and level messaging is functional and not visual-approved until references exist.

## 7. Phase 7 — Dedicated UI Phase

### Entry condition

- Sufficient functional implementation and integration from Phases 4–6 exists to exercise the core loop end to end.
- Functional navigation and a basic testable interface exist for every MVP screen.
- The user is explicitly notified: **"This is the UI face."** The user intends to switch to a preferred AI model for dedicated UI development.
- Supplied references and assets are inventoried. If references are still unavailable, stop and obtain the user's direction before inventing a visual design.

### Outcomes

- A comprehensive UI specification covering:
  1. Application design identity and UI type.
  2. Navigation hierarchy and information architecture.
  3. Every screen, window, dialog, and overlay.
  4. Layouts and component placement.
  5. Buttons, cards, tables, forms, menus, and reusable components.
  6. Interaction behavior and transitions.
  7. Typography, colors, spacing, radii, borders, and elevation.
  8. Icons, illustrations, and supplied assets.
  9. Responsive resizing, DPI scaling, and scrolling.
  10. Loading, empty, success, error, and degraded states.
  11. Accessibility and keyboard navigation.
  12. Consistency and reusable design tokens.
- Reference-conformant visual implementation applied to the functional application.
- Design direction: minimal, modern, professional, realistic like a polished desktop productivity application; clear and uncluttered; features distributed across appropriate screens; no overloaded dashboard. This is direction only and does not define a complete visual system before this phase.
- Supplied visual references remain authoritative. No borrowed identity, random icons, generated illustrations presented as supplied assets, or invented visual approval.

### Exit criteria

- The UI specification covers every MVP screen, window, dialog, overlay, and required state.
- Visual implementation matches the approved specification and supplied references.
- Visual approval status is recorded for every screen.
- Accessibility, keyboard navigation, DPI scaling, resizing, and scrolling are verified.
- No screen is claimed visually approved without a supplied reference.

## 8. Phase 8 — Hardening and Release

### Outcomes

- Final visual acceptance against the approved UI specification and supplied references.
- Code signing and release packaging.
- Full and Limited installation paths tested independently.
- Upgrade, repair, rollback, backup, restore, and uninstall documented.
- Accessibility acceptance complete.
- Performance and soak tests complete.
- Security and privacy review complete.
- Extension store submission materials and policy disclosures complete.
- User documentation, recovery guide, and uninstall guide complete.
- Diagnostics bundle and support workflow complete.
- Release notes and known limitations published.

### Exit criteria

- Functional requirements acceptance matrix complete.
- No release-blocking defect remains.
- Full and Limited mode test matrices pass.
- Upgrade data-preservation test passes with tasks, settings, history, XP, achievements, and any future token data if present.
- Accessibility checklist passes.
- Backup/restore and corruption recovery pass destructive testing.
- Installer and binaries are signed.
- Uninstall removes service, autostart, pipes, and privileged files; user data choice is honored.
- Visual approval status is accurate for every screen; any remaining visual exception is explicitly accepted by the user.
- The user approves release.

## 9. Release 2 — Motivation Expansion

Planned only after MVP evidence:

- Virtual Study Companion with supplied art and progression states.
- Daily and weekly challenges with verified criteria and ethical limits.
- Token earning, token ledger UI, reward shop, themes, and customization.
- Advanced notification center.
- Expanded achievement catalog.
- Additional profile customization.

Architecture requirement: these modules attach to existing session, ledger, and achievement events. They must not replace the core database or require a second agent.

## 10. Release 3 — Planning and Intelligence

Planned only after MVP and Release 2 evidence:

- Smart Study Planner with deadlines, estimates, availability, capacity, and explainable suggestions.
- Scheduled focus profiles and calendar-aware sessions.
- Advanced analytics and exports.
- Optional multi-browser support if justified by demand.
- Accessibility and localization expansion.

The planner must be deterministic by default. “Smart” must not mean unexplainable or falsely precise.

## 11. Dependency sequence

| Dependency | Blocks |
|---|---|
| Functional implementation and integration | Dedicated UI phase |
| Visual references and assets | Final visual styling, visual approval, reference-conformance testing, companion, themes |
| Dedicated UI phase output | Final visual acceptance, release stabilization |
| Installer technology decision | Full/Limited packaging, signing, upgrade |
| Service/agent restart spike | Full-mode supervision claims |
| Extension ID and store accounts | Browser distribution, native messaging registration |
| Code-signing certificate | Public release, SmartScreen trust |
| Windows 11 test matrix | Release validation |
| SQLite migration and backup proof | Any feature that writes user data |

## 12. Workstreams

- **Product and requirements:** scope control, acceptance, prioritization.
- **UX:** functional flows, screen inventory, reference intake, accessibility, and the dedicated UI phase.
- **Desktop:** WPF shell, MVVM presentation, accessibility, functional UI in Phases 4–6, and visual implementation in Phase 7.
- **Core domain:** sessions, tracking, tasks, ledger, achievements.
- **Platform:** agent, service, pipes, installer, startup, recovery.
- **Browser:** MV3 extension, Native Messaging, DNR rules, store readiness.
- **Data:** SQLite, migrations, backups, restore, diagnostics.
- **Quality:** test automation, destructive tests, release evidence.
- **Security and privacy:** threat model, data minimization, disclosure.

## 13. Status reporting rule

A milestone report must state:

- What was delivered with evidence.
- Which phase boundary was reached: functional implementation, dedicated UI, or release stabilization.
- What was not tested.
- What is blocked by missing references or external dependencies.
- Which requirements are now verified.
- Which decisions changed and where they were recorded.

“Done” without evidence is not an acceptable status.
