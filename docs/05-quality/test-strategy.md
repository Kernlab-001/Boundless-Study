# Test Strategy and Acceptance Plan

**Product:** Boundless Study  
**Status:** Draft for user review  
**Version:** 0.1  
**Last updated:** 2026-11-12  
**Related:** `../02-requirements/functional-requirements.md`, `../02-requirements/non-functional-requirements.md`, `../01-product/product-requirements.md`

## 1. Purpose

This document defines how Boundless Study will be verified. It covers test levels, environments, acceptance criteria, destructive testing, accessibility, performance, security, both installation modes, and release evidence.

No application code exists. Nothing in this document claims that a test has been run or passed.

## 2. Quality principles

- Test the domain rules before testing the UI.
- Test Full and Limited installation paths independently.
- Test failure and recovery paths as product features, not edge cases.
- Never accept “it works on my machine” as release evidence.
- Distinguish `PASS`, `FAIL`, `BLOCKED`, and `NOT RUN`.
- Visual tests cannot pass without supplied references. Functional tests, including tests through a temporary functional interface, do not require visual approval.
- Evidence must include build identity, environment, expected result, actual result, and status.
- A requirement without evidence is unverified.

## 3. Test levels

| Level | Scope | Examples | Evidence |
|---|---|---|---|
| Unit | Pure domain and application rules | Session state, time accounting, XP formula, ledger idempotency, streak, achievement thresholds, task transitions, timezone handling. | Automated test report. |
| Integration | Components with real SQLite, filesystem, pipes, browser bridge | EF Core migrations, backup/restore, WAL behavior, named-pipe protocol, Native Messaging framing, agent restart. | Automated report plus logs. |
| Contract | IPC and browser message schemas | Version negotiation, malformed frames, unknown types, oversized payloads, request IDs, timeouts. | Contract test report. |
| UI | WPF workflows and accessibility | Onboarding, task creation, focus session, overlay, summary, settings, recovery. | UI automation, functional screenshots, visual-approval evidence where claimed, manual script. |
| System / End-to-end | Installed product in both modes | Full install, Limited install, upgrade, session, browser block, crash recovery, uninstall. | Clean-VM evidence. |
| Recovery | Crash, power loss, agent restart, migration failure, restore | Kill points, lock-hold, corrupt checkpoint, interrupted backup, denied restart. | Recovery report with before/after state. |
| Compatibility | OS, browser, DPI, mode, and data-format combinations | Windows builds, Chrome/Edge versions, 100–200% scaling, mixed DPI, Full/Limited, upgrade/downgrade. | Compatibility matrix. |
| Manual exploratory | Usability and unusual combinations | School policy, multi-monitor, fullscreen apps, sleep, multiple users, low disk. | Recorded checklist and defects. |
| Performance | Targets in NFR | Startup, query p95, CPU, memory, soak. | Metrics capture. |
| Security | Threat controls | Pipe ACLs, origin validation, malformed input, path traversal, log redaction. | Security test report. |
| Accessibility | Assistive technology and scaling | Keyboard, Narrator, high contrast, 200% scaling, reduced motion. | Accessibility report. |
| Visual | Reference conformance | Layout, assets, states, aspect ratios. | BLOCKED until references are supplied. |

Q-001 blocks only the Visual level and reference-conformance testing. It does not block unit, integration, contract, functional UI, system/end-to-end, recovery, compatibility, performance, security, or accessibility testing against a temporary functional interface.

## 4. Test environments

### 4.1 Operating systems and hardware

- Windows 11 supported builds, x64.
- Clean VM for each installation path.
- Standard user without administrator rights.
- Local administrator with UAC.
- School-managed policy simulation with blocked service installation and blocked extension/Native Messaging.
- Reference hardware from the NFR.
- Low-resource profile if school devices are in scope.
- Dual-monitor mixed DPI, 100% and 200% scaling.
- Offline environment with network capture.
- Low disk space.
- Sleep, hibernate, lock, logoff, reboot, fast user switching.
- Antivirus/EDR-enabled environment.

### 4.2 Browsers

- Chrome stable.
- Edge stable.
- Latest two stable versions for each if release policy requires.
- Incognito allowed and disallowed.
- Extension installed, disabled, removed, and updated.
- Multiple browser windows and profiles.
- Native Messaging allowed and policy-blocked.
- Unsupported browser present as a control.

### 4.3 Data sets

- New profile with no data.
- Seeded realistic tasks and subjects.
- 100,000 sessions and 1,000,000 events for performance.
- Timezone and DST boundary data.
- XP boundary values and daily cap limits.
- Seeded token ledger entries for upgrade preservation.
- Corrupt, truncated, incompatible, and malicious backup files.
- Sensitive strings in task notes, subject names, and domains for log redaction tests.
- Windows accounts with Unicode names and data.

## 5. Functional acceptance coverage

Every MUST requirement in `functional-requirements.md` must have at least one mapped test. `requirement-test-traceability.md` is the authoritative requirement-to-test matrix; it will be updated with the automated suites as implementation proceeds. At minimum, the following areas require dedicated coverage.

### 5.1 Installation modes

| Test | Expected |
|---|---|
| Clean Limited install as standard user | No elevation, no service, core loop works. |
| Clean Full install as administrator | One elevation, service installed, health checks pass, core loop works. |
| Full-mode service stop | App reports degraded supervision, not `Protected`. |
| Full-mode agent kill | Service attempts documented restart; session recovers with no downtime credit. |
| Limited-mode agent kill | UI reports agent unavailable; user/app restart path works; no false service claim. |
| School policy blocks extension | Other features work; browser protection `Unavailable`; no repeated prompts. |
| Standard user attempts Full mode | Clear explanation; optional admin-assisted path; no privilege bypass. |
| Clean uninstall, Full | Service, task, pipes, and privileged files removed; data choice honored. |
| Clean uninstall, Limited | Per-user startup, binaries, and host registration removed; data choice honored. |
| Upgrade Limited to Full | Data preserved; service healthy; no second profile. |
| Upgrade canceled at UAC | Limited remains usable; data unchanged. |
| Upgrade failure injection | Limited remains usable; no data loss. |

### 5.2 Focus and timing

- Start, complete, end early, and abandon.
- Flexible, Balanced, Strict rules.
- Break start/end and prohibition during focus.
- Idle threshold at 5 minutes.
- Overlay pause and return.
- Lock, sleep, hibernate, resume.
- Clock forward/backward jump.
- Session crossing midnight.
- Checkpoint at 0%, 50%, 99% and forced kill.
- Duplicate start request.
- Strict with site rules and missing extension.
- Strict with no configured rules.
- Recovery older than 24 hours.
- Recovery with corrupt checkpoint.

### 5.3 Application guard

- Foreground detection below and above grace.
- Overlay keyboard access and exit.
- Multi-monitor overlay.
- Limited-mode best-effort blocking for a supported user-session target.
- Limited-mode `Unsupported` target: elevated process, protected process, fullscreen-exclusive surface, other-session target, secure desktop.
- Target coverage labels `Supported`, `Unsupported`, and `Unverified`; no `Protected` claim for the latter two.
- Overlay failure pause and persistent enforcement-failure notice.
- System-critical process rejection.
- Duplicate target normalization.
- Exception grant/expiry and limit.
- No process termination or injection.
- No window title/URL/content in data or logs.

### 5.4 Browser Guardian

- Chrome and Edge install/connection.
- DNR block and allow precedence.
- Domain validation and punycode.
- Rule cleanup on end, early exit, break, expiry.
- Reapply after extension reload/browser restart.
- Heartbeat timeout and protection gap.
- Incognito allowed/disallowed.
- School policy block.
- Native Messaging origin mismatch.
- Multiple windows/profiles.
- Malformed policy and oversized message.
- No browsing history collection.

### 5.5 Tasks and study management

- Subject create/rename/archive.
- Task create/edit/validate.
- Today manual planning.
- Task-linked session.
- Snapshot history after task rename/delete.
- Task XP once; reopen and re-complete no duplicate.
- Filter/sort.
- Empty and error states.
- No automatic scheduling.

### 5.6 Rewards and progression

- `ECONOMY-1` vectors TV-01 through TV-20 from `domain-business-rules.md`.
- XP formulas for each profile and outcome.
- 10-minute minimum, including 599 seconds.
- 300 XP daily cap, including partial remaining balance.
- Task and achievement XP outside cap.
- 500 XP level boundaries and level 50 cap.
- Streak same-day and missed-day behavior.
- Five achievements at exact thresholds.
- Ledger idempotency on duplicate events.
- Multiple achievements unlocked by one event in one transaction.
- Crash before commit, crash after commit, and retry after uncertain completion.
- Atomic transaction failure injection.
- No app-launch reward.
- `ECONOMY-1` rule version retention; no historical recalculation.
- Local date and timezone behavior.
- Token entries preserved through upgrade, backup, restore, migration.
- No token UI in MVP.
- Achievement catalog immutability: each `AchievementCode` retains one `CriteriaType` and `Threshold`; criteria changes, silent definition overwrite, deletion, and code reuse are rejected.
- Historical unlocks resolve to the retained immutable definition.
- An XP-only change is checked as a new `EconomyRuleVersion` that leaves eligibility criteria unchanged.

The achievement catalog check compares the seeded definitions with the approved catalog. It fails if an existing code's `CriteriaType` or `Threshold` differs, if a definition is missing, if a code is reused, or if an unrecorded definition change is found. Any eligibility change must appear as a new `AchievementCode`.

### 5.7 Insights and history

- Today/week/30-day boundaries.
- Subject split reconciliation.
- Session history fields.
- Definitions and insufficient-data states.
- Accessible tables.
- No composite score.
- Performance at 100,000 sessions.

### 5.8 Data management

- Automatic backup schedule and retention.
- Manual backup manifest/checksum.
- Restore valid, corrupt, truncated, incompatible.
- Safety backup before restore.
- Integrity check and quarantine.
- Migration success and forced failure.
- Export and deletion.
- Diagnostics redaction.
- ACL isolation between standard users.
- Database access boundary: UI, service, bridge, and extension cannot open the SQLite file; only the agent opens it.
- Read path: request/response IDs, timeout, cancellation, bounded pagination, consistent snapshots, and `SNAPSHOT_EXPIRED` behavior.
- Backup and migration ownership: only the agent coordinates them.
- No cloud/network data transfer.
- Uninstall keep/delete data.

### 5.9 Accessibility

- Keyboard-only core flows.
- Narrator onboarding, task, session, summary, backup.
- High contrast.
- 200% scaling and mixed DPI.
- Reduced motion.
- Focus order and focus visibility.
- Overlay accessibility and escape from trap.
- No color-only status.
- Accessible chart alternative.
- Error announcements.

### 5.10 Security and privacy

- Pipe connection from another user.
- Unapproved pipe client role.
- Malformed, oversized, slow, and unknown-version messages.
- Native Messaging origin mismatch.
- Extension permission review.
- Secret scan.
- Path traversal in restore/import.
- Backup ACL.
- Log and diagnostic seeded-string redaction.
- No network traffic.
- Service account and listener inspection.
- No kernel driver/injection/hidden persistence.
- Code-signature verification.

### 5.11 Performance and soak

- UI cold/warm start.
- Agent start.
- Foreground detection and overlay.
- Browser policy application.
- Heartbeat degradation.
- Database query p95.
- Agent/UI CPU and memory.
- Eight-hour soak with sleep/resume, browser reconnect, and repeated sessions.
- Checkpoint write load.
- 100,000-session insights.

## 6. Acceptance traceability

The authoritative requirement-to-test mapping is `requirement-test-traceability.md`. It covers all 188 functional MUST requirements, the 10 SHOULD requirements, and all 96 NFRs (87 MUST and 9 SHOULD). The matrix records the test ID, requirement ID, acceptance criterion, module, test level, preconditions, data, actions, expected result, failure conditions, evidence, release milestone, and status.

| Module | Functional requirements | Primary test suites |
|---|---|---|
| XCT | BS-XCT-001–010 | Offline, privacy, status, accessibility, architecture, database boundary. |
| ONB | BS-ONB-001–012 | Full/Limited onboarding, policy block, permissions. |
| DSH | BS-DSH-001–007 | UI, data reconciliation, states. |
| FOC | BS-FOC-001–018 | Domain timing, state machine, recovery, E2E. |
| APP | BS-APP-001–013 | App guard, overlay, process safety, exceptions, target coverage. |
| BRW | BS-BRW-001–019 | Extension, Native Messaging, DNR, policy, heartbeat. |
| STU | BS-STU-001–012 | Task domain, UI, validation, XP integration. |
| GAM | BS-GAM-001–018 | Ledger, `ECONOMY-1` vectors, levels, streak, achievements, achievement immutability, upgrade. |
| INS | BS-INS-001–010 | Aggregation, accessibility, performance. |
| DAT | BS-DAT-001–018 | SQLite, access boundary, migration, backup, restore, deletion. |
| NOT | BS-NOT-001–010 | Toast, preferences, accessibility, rate limiting. |
| SYS | BS-SYS-001–035 | Full/Limited install, service, agent, pipes, upgrade, uninstall. |
| SET | BS-SET-001–016 | Settings, health, accessibility, upgrade entry. |
| NFR | BS-NFR-REL/PERF/SEC/PRV/A11Y/COM/MNT/OBS/RLS | Reliability, performance, security, privacy, accessibility, compatibility, maintainability, observability, release. |

A test may cover more than one requirement only when its actions and expected result genuinely verify each listed requirement. The matrix does not use placeholder “covered by suite” rows. A requirement is covered only by a named test case with a measurable expected result.

## 7. Defect severity

| Severity | Definition | Release impact |
|---|---|---|
| S1 — Critical | Data loss, security breach, reward corruption, service causes system instability, or protection falsely shown as active. | Blocks release. |
| S2 — Major | Core loop broken, Full/Limited install failure, upgrade data loss, accessibility blocker, browser protection unusable without disclosure. | Blocks release. |
| S3 — Moderate | Workaround exists; secondary feature impaired; recovery confusing but safe. | May ship only with explicit user acceptance. |
| S4 — Minor | Cosmetic or copy issue with no functional or trust impact. | May ship with known-issues note. |

No open S1 or S2 defect may be waived by the implementing agent. User approval is required for any known-issue exception.

## 8. Test evidence format

Each executed test records:

- Test ID and requirement ID.
- Build version and commit/build identifier.
- Operating system, browser, install mode, account type.
- Preconditions and data set.
- Steps or automation reference.
- Expected result.
- Actual result.
- Status: `PASS`, `FAIL`, `BLOCKED`, or `NOT RUN`.
- Evidence path: log, screenshot after visual approval, report, or recording.
- Defect ID, if any.
- Date and tester.

## 9. Release gates

The MVP cannot be released until:

1. All MUST requirements have `PASS` evidence or a user-approved documented exception.
2. Full and Limited installation paths have independent clean-VM evidence.
3. Limited-to-Full upgrade preserves tasks, settings, history, XP, achievements, and any future token data if present.
4. Crash, power-loss, backup, restore, and migration tests pass.
5. No S1 or S2 defect remains open.
6. Accessibility checklist passes.
7. Security and privacy checklist passes.
8. Performance targets are measured and accepted or explicitly revised.
9. Browser extension store requirements and permission disclosures are complete.
10. Installer and binaries are signed.
11. Every screen is either visually approved against a supplied reference or explicitly accepted by the user as `BLOCKED — AWAITING USER REFERENCES`.
12. Release notes identify deferred features and known limitations.
13. The user explicitly approves release.

Visual approval is not a Phase 3 technical-validation exit criterion. It is produced by the dedicated UI phase after sufficient functional implementation and integration. Q-001 blocks only visual approval and reference-conformance testing; it does not block functional implementation or functional verification through a temporary interface.

## 10. Test status summary

| Area | Status |
|---|---|
| Unit tests | NOT STARTED — no code |
| Integration tests | NOT STARTED — no code |
| UI tests | NOT STARTED — no code; temporary functional interfaces permitted |
| Full-mode install | NOT STARTED — no implementation |
| Limited-mode install | NOT STARTED — no implementation |
| Upgrade preservation | NOT STARTED — no implementation |
| Browser extension | NOT STARTED — no implementation |
| Accessibility | NOT STARTED — no implementation |
| Performance | NOT STARTED — no implementation |
| Security | NOT STARTED — no implementation |
| Requirement traceability matrix | DRAFT — tests defined, none executed |
| Visual approval | BLOCKED — AWAITING USER REFERENCES |
| Release | NOT AUTHORIZED |
