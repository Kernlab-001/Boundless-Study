# Boundless Study — Requirements and Phase 3 Validation

**Status:** Phase 2 baseline approved/frozen; Phase 3 conditionally complete  
**Phase:** 2–3  
**Last updated:** 2026-11-19 (D-012–D-017 approval record only)  
**Implementation status:** Not started. No source code, solution, scaffolding, dependencies, or visual design has been created.

## 1. Purpose

This directory is the planning source of truth for the Boundless Study MVP. It converts the approved product context into testable requirements, domain rules, UX flows, a conceptual data model, system architecture, decision records, and a test strategy.

The MVP is one complete loop:

> Plan → Focus → Block → Measure → Reward → Review

## 2. Approved decisions

| ID | Decision | Status |
|---|---|---|
| D-001 | First release is a core-loop MVP: basic task planning, focus sessions, distraction blocking, study-time tracking, XP progression, basic rewards, and progress review. | Approved by user |
| D-002 | Advanced planner, virtual companion, daily/weekly challenges, token shop, and themes are deferred. Architecture must allow later addition without major restructuring. | Approved by user |
| D-003 | Planning-only documents may be created in this project folder. No source code, project scaffolding, dependency installation, or implementation is allowed. | Approved by user |
| D-004 | Phase 2 proceeds without UI references. Functional and visual specifications are separated. Every unresolved visual decision is marked blocked in the UI Reference Gap Log. | Approved by user |
| D-005 | MVP supports two installation modes: Full mode with an elevated Windows service, and Limited mode without administrator rights. | Approved by user |
| D-006 | Visual identity, colors, typography, spacing, imagery, icons, themes, and final layouts are not invented. Supplied references are authoritative. | Approved by user |
| D-007 | Full mode installs the Windows service and supports stronger service-supervised application enforcement. Limited mode runs per-user without a service and must never present weaker protection as active. | Approved by user |
| D-008 | Setup must detect available permissions, explain both modes before selection, avoid repeated elevation prompts in Limited mode, and allow a documented upgrade from Limited to Full mode. | Approved by user |
| D-009 | Both modes share the same local account and data architecture. Upgrading must preserve tasks, settings, study history, XP, achievements, and any future token data if present. Token earning and spending are not part of the MVP. Shared application functionality must not depend on the privileged service. | Approved by user |
| D-010 | Both installation paths are independently testable release paths. School-managed browser restrictions must fail visibly, not silently. | Approved by user |
| D-011 | After implementation is explicitly authorized, implementation is functionality-first. Working business logic, application services, persistence, the focus engine, application and browser enforcement; XP, achievements, and progression; and IPC and Windows integration come before visual refinement. A basic functional UI is permitted for operation and testing but is not final visual design. Visual refinement is deferred to the dedicated UI phase after sufficient functional implementation and integration, and before final visual acceptance and release stabilization. | Approved by user |
| D-012 | Session-event payloads are limited to a maximum of 2,000 characters (Milestone 3 decision A1). | Approved by user (2026-11-19) |
| D-013 | The application-rule version is app-owned and fixed as `APP-RULES-1`; callers do not supply it (A2). | Approved by user (2026-11-19) |
| D-014 | The protected executable baseline is approved as 24 targets and is grow-only. On 2026-11-22, the user approved adding `VoiceAccess.exe`, `LiveCaptions.exe`, and `AtBroker.exe` to the 21 existing names, completing the approved 24-target baseline in `BlockingRules.cs`. Windows 11 accessibility coverage must be verified before release or before blocking is enabled (A3). | Approved by user (2026-11-19, additions approved 2026-11-22) |
| D-015 | `Interrupted` and `Abandoned` are approved additive session-event types; existing event history remains compatible (A4). | Approved by user (2026-11-19) |
| D-016 | An unresolved recoverable interrupted session blocks starting a new focus session. The app offers Resume or Discard and abandons the session automatically after 24 hours (A6). | Approved by user (2026-11-19) |
| D-017 | Achievement eligibility uses lifetime completed-task history. Archiving or soft-deleting a task must not erase its historical contribution; reopening a task must not erase an already-earned achievement or duplicate rewards (ACH-005). | Approved by user (2026-11-19) |

> D-012-D-017 record product decisions only. They do not assert final acceptance. The Milestone 3 closure implemented and regression-tested A1, A2, A4, A6 and ACH-005, including the v1/v2-to-v3 schema upgrade. On 2026-11-22, the user approved adding `VoiceAccess.exe`, `LiveCaptions.exe`, and `AtBroker.exe` to reconcile the A3 baseline to exactly 24 targets, which is now implemented and tested. Live Windows 11 accessibility coverage remains an external release condition. See `Reviews/MILESTONE-3-VERIFICATION.md` for the evidence, the outstanding release conditions and the verdict.

## 3. Product boundaries in force

- Windows desktop, WPF, C#, .NET 10, MVVM.
- SQLite with Entity Framework Core.
- Chromium Manifest V3 extension in TypeScript.
- Native Messaging between the extension and the desktop.
- Named Pipes between desktop components.
- An independently executing Windows background service in Full mode.
- Local-first operation with no mandatory account and no mandatory cloud connection.
- Best-effort enforcement. No kernel driver, stealth persistence, hidden monitoring, or impossible tamper-resistance claim.
- Chrome and Edge are the MVP browsers.
- English is the MVP UI language.
- No mobile, cloud sync, social features, or real-money economy in the MVP.

## 4. Document map

| Path | Purpose | Status |
|---|---|---|
| `01-product/product-requirements.md` | Product Requirements Specification: vision, personas, MVP scope, product risks, assumptions, success criteria. | Draft |
| `01-product/development-roadmap.md` | Phased delivery roadmap with gates and deferred releases. | Draft |
| `02-requirements/functional-requirements.md` | Module specifications, functional requirement IDs, and testable acceptance criteria. | Draft |
| `02-requirements/non-functional-requirements.md` | Reliability, performance, security, privacy, accessibility, maintainability, and supportability requirements. | Draft |
| `02-requirements/domain-business-rules.md` | Focus-session state machine, time accounting, blocking behavior, reward rules, XP economy, streaks, achievements, and ledger rules. | Draft |
| `02-requirements/conceptual-data-model.md` | Conceptual entities, relationships, constraints, retention, and migration model. | Draft |
| `03-ux/ux-specification.md` | Information architecture, navigation, user journeys, screen inventory, functional states, interactions, and content requirements. | Draft; visual details blocked |
| `03-ux/ui-reference-gap-log.md` | Missing visual references and assets, with required purpose and approval status. | Open |
| `04-architecture/system-architecture.md` | Process model, containers, components, data ownership, IPC, runtime flows, deployment modes, and failure handling. | Draft |
| `04-architecture/adr/` | Architecture Decision Records. | Draft |
| `05-quality/test-strategy.md` | Test levels, acceptance strategy, environments, quality gates, and release evidence. | Draft |
| `05-quality/requirement-test-traceability.md` | Requirement-to-test matrix for every functional MUST, the SHOULD requirements, and every NFR. | Draft |
| `06-user/user-guide.md` | Draft user-facing guide for installation modes, daily use, recovery, data, privacy, and uninstall. Records the Full/Limited capability differences for users. | Draft; visual details blocked |
| `07-phase-3/phase-3-validation-record.md` | Phase 3 technical validation plan, spike register, evidence, ADR updates, compatibility matrix, risks, open-question dispositions, and completion report. | Executed; conditional completion |

## 5. Authority and precedence

When documents conflict, apply this order:

1. The user's current written instruction.
2. Approved decisions recorded in this README and in ADRs.
3. `product-requirements.md`.
4. `domain-business-rules.md`.
5. `functional-requirements.md`.
6. `non-functional-requirements.md`.
7. UX, data-model, architecture, and test documents.
8. Supplied UI references for visual truth.

For visual matters, a supplied reference outranks any document description. For functional matters, a reference does not override requirements, safety rules, or privacy limits.

If a conflict is found, stop and record it rather than silently choosing one document.

## 6. Requirement conventions

- **MUST** means required for MVP acceptance.
- **SHOULD** means expected unless a documented reason prevents it.
- **MAY** means optional within MVP.
- Requirement IDs use `BS-<MODULE>-###`.
- Acceptance criteria IDs use `AC-<MODULE>-###`.
- ADR IDs use `ADR-####`.
- Visual gaps use `UIG-###`.
- A statement is not complete until it has a verification method. Untested requirements are marked as unverified, not passed.

## 7. Open questions and resolved decisions

| ID | Item | Current handling |
|---|---|---|
| Q-001 | UI references, icons, illustrations, and final visual specifications are absent. | **Open; visual-only blocker.** Blocks final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing. Does not block functional requirements, technical validation, business logic implementation, database implementation, functional navigation, temporary testing interfaces, or feature-level automated testing. If references remain unavailable at the dedicated UI phase, stop and obtain the user's direction before inventing a visual design. |
| Q-002 | Installer technology is not selected. | **Provisionally resolved; user approval required.** Inno Setup 7.x with 64-bit install mode is recommended. Limited-mode lifecycle was verified with an Inno Setup 6.0.5 test build; the closure round executed the elevated Full-mode install, service registration/removal, elevated uninstall, and data preservation, and discovered the Wow6432Node registry-redirection requirement (R-P3-13). Upgrade, repair, rollback, and signing remain in the elevated runbook. See `07-phase-3/phase-3-validation-record.md` §3.10 and §4. |
| Q-003 | Exact service-to-agent restart mechanism must be validated on Windows. | **Partially resolved (closure round); session-0 remainder deferred with owner.** The supervision loop was verified in user mode: per-user logon-trigger task via the Task Scheduler COM API, Task Scheduler launch, control-pipe heartbeat, agent crash detection and rate-limited restart, visible degraded state, and supervisor-crash adoption. The remaining session-0 behaviors (service execution, logon/logoff/FUS/sleep/resume/reboot, multi-session) require a real service binary on an elevated machine; owner and runbook are in `07-phase-3/phase-3-validation-record.md` §3.13. |
| Q-004 | Chrome Web Store and Edge Add-ons publishing accounts, fixed extension ID, and code-signing certificate are not confirmed. | **Partially verified; external dependencies remain with owners and completion conditions.** Fixed manifest-key ID, HKCU registration, host launch, and ping/pong were verified in Chrome and Edge. The closure round documented both stores' requirements and Google's official ID-stability workflow (`07-phase-3/phase-3-validation-record.md` §3.12). Store accounts, final store ID, review, and code signing remain external. |
| Q-005 | Exact XP values, level curve, and thresholds require playtesting. | **Resolved:** `ECONOMY-1` is frozen for MVP acceptance in `02-requirements/domain-business-rules.md`. Future tuning requires a new economy version; historical ledger entries are never recalculated. |
| Q-006 | Minimum supported Windows 11 builds and browser versions need confirmation. | **Resolved with conditions.** Windows 11 x64 24H2, 25H2, and 26H1 are the baseline per the .NET 10 supported-OS list. Windows 11 26H2 is conditional until listed by Microsoft. Chrome stable and Edge stable are the browser channels; version 157 was verified locally. Windows 10, ARM64, and Server are out of MVP scope. |
| Q-007 | How achievement eligibility changes are versioned and how historical unlocks remain interpretable. | **Resolved:** An `AchievementCode` permanently identifies one eligibility definition. `CriteriaType` and `Threshold` must not change, be overwritten, or be deleted under an existing code, and achievement definitions must not be silently overwritten or deleted. Changing eligibility requires a new `AchievementCode`; an XP-only change requires a new economy version and leaves eligibility unchanged. Historical `UserAchievement` records retain their original `AchievementCode` and resolve to the retained immutable definition. No criteria version or unlock-time criteria snapshot is added for the MVP. Replacement, migration, or retirement that could create duplicate rewards requires a separately documented decision before implementation. |

Seven questions are recorded. Q-005 and Q-007 are resolved. Q-006 is resolved with conditions. Q-001 remains open as a visual-only blocker. Q-002 is provisionally resolved as a recommendation requiring user approval. Q-003 is partially resolved: the supervision loop is verified in user mode and the session-0 remainder has an explicit owner and runbook. Q-004 is partially verified with documented store requirements and external account-dependent tasks.

## 8. Phase 2 correction status

The Phase 2 baseline was corrected against the independent audit findings and the additional documentation change requests. Documentation corrections do not authorize implementation.

| Finding | Status | Evidence |
|---|---|---|
| C-01 priority conflicts | Closed in documentation | Five pairs resolved by narrowing secondary scopes; priorities remain 188 MUST / 10 SHOULD / 0 MAY. |
| C-02 Limited-mode application blocking | Closed in documentation | SRS §7.4, ADR-0007, capability matrix, UX, user guide, architecture, and domain rules now describe best-effort per-user enforcement and its limits. |
| C-03 database read path | Closed in documentation | ADR-0005 and ADR-0006 are accepted with conditions; the agent is the sole owner and only process that opens the database. |
| C-04 requirement-level test traceability | Closed in documentation | `05-quality/requirement-test-traceability.md` maps all functional requirements and NFRs. No test has been executed. |
| C-05 economy versioning | Closed in documentation | `ECONOMY-1` is frozen with parameterized vectors; economy versioning is recorded in the domain rules and ADR-0006. |
| C-06 acceptance-criteria quality | Closed in documentation | The named vague criteria now use observable states, thresholds, formats, or test methods. |
| C-07 token-preservation wording | Closed in documentation | D-009 now preserves future token data only if present and states that token earning and spending are not part of the MVP. |
| C-08 token-scope consistency | Closed in documentation | MVP documents consistently exclude token awards, balances, spending, shops, and token UI; schema and upgrade rules preserve future token data. |
| C-09 final cross-document consistency | Closed in documentation | Requirement counts, IDs, acceptance criteria, test mappings, mode capabilities, database ownership, economy versioning, ADR statuses, open questions, phase gates, and visual blocking were audited; contradictions found in the audit were corrected. |
| C-10 achievement-definition and economy versioning | Closed in documentation | Q-007 resolved: achievement codes permanently identify immutable eligibility definitions; criteria changes require a new code; XP-only changes use a new economy version; no criteria version or snapshot is added. |
| C-11 functionality-first development boundary | Closed in documentation | D-011 records functionality-first implementation. Roadmap Phases 4–6 build working functionality and permit a basic functional UI; Phase 7 is the dedicated UI phase, entered with the explicit notification "This is the UI face." Q-001 is limited to visual work, and Phase 3 remains technical validation. |

The Phase 2 archive is the approved/frozen baseline for Phase 3. Phase 3 technical validation is conditionally complete; see `07-phase-3/phase-3-validation-record.md`. Phase 4 remains unauthorized.

### 8.1 Phase 3 execution status

| Area | Status | Evidence |
|---|---|---|
| Compatibility and Q-006 | Resolved with conditions | Windows 11 x64 24H2/25H2/26H1; Chrome/Edge stable; 26H2 conditional; Windows 10/ARM64/Server out of scope |
| Browser MV3, fixed ID, DNR, Native Messaging | Verified | Chrome/Edge 157; Edge restart persistence; HKCU host launch and ping/pong |
| SQLite WAL, backup, crash recovery | Verified at engine level | SQLite 3.49.1; EF Core/migrations blocked without .NET SDK |
| Ledger idempotency and atomicity | Verified at engine level | Unique key, rollback, concurrent duplicate prevention |
| Named-pipe framing, concurrency, reconnect | Verified | 200 correlated requests; timeout/cancel; malformed-frame rejection; message mode and DACL |
| Limited-mode installer | Verified | Inno Setup 6.0.5 test build; install, upgrade, uninstall, HKCU registration, data preservation |
| Full-mode installer (closure round) | Partially verified | Elevated install/uninstall, service registration/removal, and data preservation executed; Wow6432Node redirection found (64-bit mode required); upgrade/repair/rollback remain |
| Full-mode supervision (closure round) | Loop verified in user mode | Task-Scheduler launch, heartbeat, crash restart, adoption, rate-limited degraded state; session-0 service execution remains |
| EF Core 10 integration (closure round) | Verified | Migrations, handshake, tamper detection, transactions, concurrency, backup/restore, WAL, interrupted migration (rollback clean; recovery completes after stale-lock clear — R-P3-16), v1→v2 upgrade |
| Store publishing requirements (closure round) | Documented | CWS fee/account; Edge Partner Center/MSA; official ID-stability workflow |
| Guard boundaries (closure round) | Verified at PoC level | Monitor enumeration, foreground identification, fullscreen loop (negative in-run), detection latency avg 5.349 ms / max 8.002 ms at 100 ms polling |
| Store accounts and code signing | Blocked (external) | Fixed ID verified; final store ID and certificate remain external |
| Q-002 | Recommended, user approval required | Inno Setup 7.x with 64-bit mode; Full-mode lifecycle partially validated |
| Q-003 | Partially resolved | Supervision loop verified in user mode; session-0 remainder owned via elevated runbook |
| Q-004 | Partially verified | ID/registration/Native Messaging verified; store/signing external with owner and runbook |

No Phase 3 result changes the frozen requirements, acceptance criteria, economy rules, database definitions, or architecture decisions. No status of an ADR was changed without approval. The product-decision approvals recorded on 2026-11-19 (§2, D-012–D-017) are separate from the Phase 3 results and do not change any ADR status.

## 9. Review rule

No visual screen is approved without an actual supplied reference. Q-001 blocks visual work only: final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing. It does not block functional requirements, technical validation, business logic, database work, functional navigation, temporary testing interfaces, or feature-level automated testing. No source code begins until the user explicitly authorizes implementation after reviewing this documentation set.

## 10. Reference-tooling note

The gstack and unslop archives were inspected, but neither is installed or registered for the executing agent. No gstack or unslop workflow has been claimed or run. Unslop is a prose-audit aid, not a technical correctness gate.
