# Functional Requirements and Module Specifications

**Product:** Boundless Study  
**Status:** Draft for user review  
**Version:** 0.2  
**Last updated:** 2026-11-12  
**Related:** `domain-business-rules.md`, `non-functional-requirements.md`, `../03-ux/ux-specification.md`, `../05-quality/test-strategy.md`

## 1. Purpose

This document defines the MVP functional baseline. Each requirement has a stable ID, priority, a testable statement, and acceptance criteria. A requirement is not complete until its acceptance criteria have evidence.

Priority meanings:

- **MUST:** required for MVP release.
- **SHOULD:** required unless an approved exception is recorded.
- **MAY:** optional within MVP.

Requirement conventions:

- `BS-<MODULE>-###` is the requirement ID.
- `AC-<MODULE>-###` is the acceptance criteria ID.
- “Visible” means presented through accessible UI or an accessible system notification, not merely written to a log.
- Visual styling is not specified here. See `../03-ux/ui-reference-gap-log.md`.

## 2. Module map

| Code | Module | MVP role |
|---|---|---|
| XCT | Cross-cutting | Local-first, privacy, truthfulness, accessibility, integrity. |
| ONB | Onboarding and Diagnostics | Permissions, mode explanation, extension setup, first-run flow. |
| DSH | Dashboard | Today, quick start, status, recent activity. |
| FOC | Focus Engine | Sessions, profiles, timing, breaks, recovery. |
| APP | Application Guard | Blocked applications and overlay behavior. |
| BRW | Browser Guardian | Chrome/Edge extension, site rules, connection status. |
| STU | Study Manager | Subjects, tasks, today planning, task-linked sessions. |
| GAM | Gamification and Progression | XP, levels, streak, achievements, ledger. |
| INS | Insights | Daily/weekly totals, subject split, session history. |
| DAT | Data Management | SQLite, migrations, backups, export, restore, deletion. |
| NOT | Notifications | Minimal local alerts. |
| SYS | System Integration | Modes, agent, service, bridge, pipes, startup, recovery. |
| SET | Settings and Accessibility | Configuration, mode status, diagnostics, accessibility. |

---

## 3. Cross-cutting requirements

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-XCT-001 | MUST | Core study features operate without a network connection after installation. | AC-XCT-001: With networking disabled, a user can create a task, start and complete a focus session, earn XP, review insights, and restore a backup. |
| BS-XCT-002 | MUST | No telemetry, analytics, or personal data is transmitted by default. | AC-XCT-002: A network capture during a complete core-loop session shows no outbound application telemetry. |
| BS-XCT-003 | MUST | The product uses no hidden persistence, stealth monitoring, kernel driver, process injection, or undocumented Windows modification. | AC-XCT-003: Installed components, startup entries, service, native host, and extension are documented and removable; inspection finds no hidden autostart or injected code. |
| BS-XCT-004 | MUST | Protection status is truthful. | AC-XCT-004: For every combination of missing extension, stopped agent, stopped service, policy block, and unavailable overlay, the UI never displays `Protected`. |
| BS-XCT-005 | MUST | The user owns local data and can export, back up, restore, and delete it. | AC-XCT-005: Each operation ends in exactly one visible state: `Succeeded` with the integrity result and output path, `Cancelled` with no data change, or `Failed` with a stable error code, no partial mutation, and a recovery action. |
| BS-XCT-006 | MUST | Core workflows are keyboard operable and screen-reader understandable. | AC-XCT-006: Keyboard-only and Narrator acceptance scripts complete onboarding, subject and task creation, task completion, session start/end, backup, and settings changes. |
| BS-XCT-007 | MUST | Failures produce an actionable user-visible state; silent failure is prohibited. | AC-XCT-007: Each documented failure state has a message, recovery action, and diagnostic event. |
| BS-XCT-008 | MUST | IPC messages, extension messages, database schemas, and economy rules are versioned. | AC-XCT-008: An incompatible version is rejected with a stable `INCOMPATIBLE_VERSION` code, the sender and receiver versions, the affected capability, and the required update or recovery action; no partial data mutation occurs. |
| BS-XCT-009 | MUST | The per-user agent is the sole database owner and the only process that opens the study database, for reads and writes. | AC-XCT-009: Code and integration tests show no UI, service, bridge, or extension process opens the database file; all reads and writes go through agent IPC. |
| BS-XCT-010 | MUST | No screen is visually approved without a supplied reference. | AC-XCT-010: The UI Reference Gap Log status matches the actual reference inventory for every screen. |

---

## 4. Onboarding and Diagnostics (ONB)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-ONB-001 | MUST | First run explains local-first operation, data location, privacy, and the absence of a mandatory account. | AC-ONB-001: The explanation is shown before mode selection and can be revisited from Settings. |
| BS-ONB-002 | MUST | Setup detects Windows version, architecture, elevation state, administrator eligibility, existing installation, existing data, installed browsers, extension presence, Native Messaging availability, and service capability. | AC-ONB-002: The diagnostics result distinguishes supported, unsupported, unavailable-by-policy, and not-installed states. |
| BS-ONB-003 | MUST | Both Full and Limited modes are explained before selection, including enforcement differences, admin requirements, browser limits, data location, and uninstall/recovery. | AC-ONB-003: The mode comparison communicates every row of the capability matrix in the SRS. |
| BS-ONB-004 | MUST | The user explicitly selects Full or Limited mode. Setup does not silently choose Full mode. | AC-ONB-004: Canceling mode selection leaves the app uninstalled or in a safe pre-onboarding state; no privileged component is installed. |
| BS-ONB-005 | MUST | Full mode requests elevation only for the privileged installation step and explains why. | AC-ONB-005: Declining elevation shows two labeled actions, “Continue in Limited mode” and “Cancel installation”; choosing Limited leaves existing data unchanged and completes Limited onboarding without a second elevation prompt. |
| BS-ONB-006 | MUST | Limited mode never triggers repeated administrator prompts. | AC-ONB-006: A standard-user account completes Limited onboarding and a full session without an elevation prompt. |
| BS-ONB-007 | MUST | Browser setup guides the user through installing the Chrome or Edge extension and confirms connection. | AC-ONB-007: The app distinguishes installed-and-connected, installed-but-disconnected, not-installed, and blocked-by-policy states. |
| BS-ONB-008 | MUST | If browser policy blocks extension installation or Native Messaging, onboarding explains the limitation and allows the remaining features. | AC-ONB-008: The user can finish Limited onboarding and start a focus session with browser protection marked `Unavailable`. |
| BS-ONB-009 | MUST | Onboarding creates or offers a first subject and first task. | AC-ONB-009: A valid first task can be created, skipped, or edited later without blocking onboarding. |
| BS-ONB-010 | MUST | Before the first focus session, the user sees a plain-language explanation of what is blocked, what is not, how to end a session, and how to recover or uninstall. | AC-ONB-010: The first-session tutorial includes all four topics and records acknowledgement. |
| BS-ONB-011 | SHOULD | Onboarding can resume after app closure without losing completed steps. | AC-ONB-011: Restarting during browser setup resumes at the same stored onboarding step with all previously completed steps preserved; the stored step ID matches the step shown. |
| BS-ONB-012 | MUST | Diagnostics can be rerun from Settings without reinstalling. | AC-ONB-012: Rerunning updates mode, extension, bridge, agent, and service health. |

---

## 5. Dashboard (DSH)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-DSH-001 | MUST | Dashboard shows today’s active-study time, elapsed focus time, XP, level, and current streak with clear labels. | AC-DSH-001: Values match domain calculations for a seeded session history. |
| BS-DSH-002 | MUST | Dashboard shows today’s planned tasks and allows opening a task or starting focus from it. | AC-DSH-002: A task planned for today appears in the today list and can launch a task-linked session. |
| BS-DSH-003 | MUST | Dashboard provides a quick-start path with profile and duration selection. | AC-DSH-003: Quick start requires a profile and duration and opens the pre-session summary. |
| BS-DSH-004 | MUST | Dashboard shows installation mode, service health if Full, agent health, and browser protection status. | AC-DSH-004: Every status maps to a domain status; none is inferred from a generic “running” flag. |
| BS-DSH-005 | MUST | Dashboard shows recent completed or ended sessions with task, profile, active time, and outcome. | AC-DSH-005: Selecting a recent session opens its summary. |
| BS-DSH-006 | MUST | Dashboard has loading, empty, partial-data, and error states. | AC-DSH-006: No-data and database-unavailable states are distinct and actionable. |
| BS-DSH-007 | SHOULD | Dashboard does not imply that opening the application earns progress. | AC-DSH-007: Launching the app repeatedly changes no XP, streak, or achievement value. |

---

## 6. Focus Engine (FOC)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-FOC-001 | MUST | A user can start a focus session with a selected profile, duration, and optional task. | AC-FOC-001: A valid session is created once with all selected values and appears as Active. |
| BS-FOC-002 | MUST | A pre-session summary shows duration, profile behavior, blocked app/site counts, browser status, exceptions policy, and enforcement limitations. | AC-FOC-002: The summary is shown before enforcement starts and cannot claim unavailable protection. |
| BS-FOC-003 | MUST | Flexible, Balanced, and Strict implement the behavior matrix in the domain rules. | AC-FOC-003: Automated and manual tests verify each profile’s blocking, early-exit, exception, and XP rules. |
| BS-FOC-004 | MUST | The focus countdown uses session elapsed time and pauses on lock, sleep, and blocking overlay. | AC-FOC-004: Timer behavior matches the domain rules across sleep, lock, overlay, and resume tests. |
| BS-FOC-005 | MUST | Active-study time stops after five minutes without input and resumes on input. | AC-FOC-005: Idle simulation produces the expected active, idle, and elapsed totals. |
| BS-FOC-006 | MUST | Focus sessions integrate application guard and browser guard according to the profile. | AC-FOC-006: Starting Balanced or Strict applies configured rules; ending the session removes them. |
| BS-FOC-007 | MUST | Lock, sleep, shutdown, and clock changes are handled without crediting downtime. | AC-FOC-007: A 30-minute sleep does not increase elapsed or active time; a clock jump does not change accumulated duration. |
| BS-FOC-008 | MUST | A break session can start only when no focus session is active; blocking is lifted during the break. | AC-FOC-008: Break start is rejected while a focus session is active; on break start, application and website blocking is disabled; blocking is reapplied only when the next focus session starts. |
| BS-FOC-009 | MUST | Early exit follows the selected profile’s confirmation and cooldown rules and records a neutral end reason. | AC-FOC-009: Flexible exit is immediate; Balanced requires 10 seconds; Strict requires typed confirmation and 30 seconds; all produce `EndedEarly`. |
| BS-FOC-010 | MUST | Temporary exceptions follow profile limits, expiry, and logging rules. | AC-FOC-010: Exceptions expire automatically; the block resumes; the count cannot exceed the profile limit. |
| BS-FOC-011 | MUST | Sessions checkpoint at least every 10 seconds and recover from agent or application interruption. | AC-FOC-011: A forced agent kill loses no more than one checkpoint interval and credits no downtime; recovery reapplies restrictions. |
| BS-FOC-012 | MUST | A completed session produces a summary with planned, elapsed, active, idle, protection-gap, violation, profile, and outcome values. | AC-FOC-012: Summary values match the database and domain rules for seeded cases. |
| BS-FOC-013 | MUST | At most one focus session and one break session may be active per local profile. A pending `RecoverableInterrupted` session counts as active: the user must Resume or Discard it, and it is automatically abandoned after 24 hours (A6, approved 2026-11-19). | AC-FOC-013: Duplicate start requests, including while a recoverable interrupted session is pending, are rejected or idempotently ignored without creating a second record. |
| BS-FOC-014 | MUST | Strict cannot start with website rules when the extension/bridge is not verified connected. | AC-FOC-014: The user receives an explanation and can install/reconnect, switch profile, or remove site rules. |
| BS-FOC-015 | MUST | MVP does not expose a user-initiated pause that stops enforcement without ending the session. | AC-FOC-015: Only system pause and blocking-overlay pause are available; no pause control bypasses blocking. |
| BS-FOC-016 | MUST | Starting a session does not award XP. | AC-FOC-016: Starting and immediately ending with less than 10 active minutes produces 0 session XP. |
| BS-FOC-017 | MUST | The session UI always shows the current enforcement status and the time remaining. | AC-FOC-017: The UI reflects an agent state change within 1 second; app detection meets BS-NFR-PERF-004 and browser heartbeat degradation meets BS-NFR-PERF-006. |
| BS-FOC-018 | SHOULD | Session summaries can be reopened from history without changing rewards. | AC-FOC-018: Reopening a summary does not create a ledger entry. |

---

## 7. Application Guard (APP)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-APP-001 | MUST | Users can add, edit, disable, and remove blocked applications from installed or running applications and a file picker. | AC-APP-001: Added applications persist and appear in the pre-session summary. |
| BS-APP-002 | MUST | Application targets are normalized and duplicate paths/names are merged or warned. | AC-APP-002: Case and path normalization prevent duplicate rules for the same executable. |
| BS-APP-003 | MUST | System-critical processes, the app, agent, service, native bridge, and accessibility tools are rejected as targets. | AC-APP-003: Adding a protected component is rejected; the message names the component category, states that it is excluded for safety or accessibility, and creates no target row. |
| BS-APP-004 | MUST | The agent detects a configured blocked application entering the foreground and applies a 3-second grace period. | AC-APP-004: Detection occurs within the declared target, and short switches under 3 seconds do not show an overlay. |
| BS-APP-005 | MUST | After grace, the agent displays a blocking overlay naming the application and offering return-to-study and end-session actions. | AC-APP-005: Overlay appears on the active monitor and provides keyboard-accessible actions. |
| BS-APP-006 | MUST | Session elapsed and active time pause while the overlay is active. | AC-APP-006: Time spent on the overlay is not credited to either timer. |
| BS-APP-007 | MUST | The agent never terminates, suspends, injects into, or modifies a blocked process. | AC-APP-007: Process inspection shows no termination or injection during a violation. |
| BS-APP-008 | MUST | Violations record target identifier, start/end time, duration, and count; window titles and content are not stored. | AC-APP-008: Database inspection finds no window title, URL, or document content from a violation. |
| BS-APP-009 | MUST | Exceptions suppress the overlay for the targeted application for the allowed duration, then reapply. | AC-APP-009: Exception expiry restores blocking without restarting the session. |
| BS-APP-010 | MUST | Full mode provides service-supervised agent recovery; Limited mode implements best-effort enforcement through the per-user agent when the supported Windows mechanisms are available, without service supervision. | AC-APP-010: Full mode with a healthy service shows `Supervised`; Full mode with a stopped or unhealthy service shows degraded supervision; Limited mode shows `Best effort`; both report supported and unsupported application targets and neither claims system-wide or tamper-proof enforcement. |
| BS-APP-011 | MUST | If the overlay cannot be displayed, the session pauses, an enforcement failure is recorded, and the user is notified. | AC-APP-011: Simulated overlay failure stops time accumulation and produces a visible recovery state. |
| BS-APP-012 | MUST | Fullscreen-exclusive, elevated, and protected applications that prevent detection or overlay are documented limitations and reported as `Unsupported` or `Unverified`. | AC-APP-012: The limitation appears in onboarding and pre-session disclosure, not only in release notes; affected targets are never shown as `Protected`. |
| BS-APP-013 | MUST | Application blocking is active only during focus, not during breaks or after a terminal session. | AC-APP-013: Break and session-end tests show no overlay for blocked apps. |

---

## 8. Browser Guardian (BRW)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-BRW-001 | MUST | Setup detects Chrome and Edge separately and does not claim support for other browsers in MVP. | AC-BRW-001: Unsupported browsers are shown as unsupported; no protection status is attributed to them. |
| BS-BRW-002 | MUST | The app guides extension installation through the supported browser mechanism; it does not silently install the extension. | AC-BRW-002: The user completes installation; the app detects the extension and connection. |
| BS-BRW-003 | MUST | Native Messaging registration is per-user where supported and uses the fixed extension ID and allowlisted origin. | AC-BRW-003: A mismatched extension ID is rejected; the expected host connects. |
| BS-BRW-004 | MUST | The app shows extension installed, connected, policy applied, heartbeat age, last error, and protection status. | AC-BRW-004: Each state is distinguishable; a stale heartbeat becomes `Degraded` within 30 seconds. |
| BS-BRW-005 | MUST | Active site rules are pushed on session start and include blocked domains, allowed domains, session ID, and expiry. | AC-BRW-005: Chrome and Edge apply the expected rules for the active session. |
| BS-BRW-006 | MUST | Explicit allow rules take precedence over blocked domains. | AC-BRW-006: A domain in both lists is allowed; the UI explains precedence. |
| BS-BRW-007 | MUST | Domains are normalized and validated; unsupported schemes, paths, wildcards, and invalid hostnames are rejected. | AC-BRW-007: Valid domains are accepted; invalid input produces a field-level error. |
| BS-BRW-008 | MUST | Rules are removed on session completion, early exit, abandonment, break start, or expiry. | AC-BRW-008: After each terminal transition, blocked sites are reachable again and no stale rules remain. |
| BS-BRW-009 | MUST | Rules are reapplied after browser restart, extension service-worker wake, or extension update. | AC-BRW-009: Reloading the extension during an active session restores the expected rules. |
| BS-BRW-010 | MUST | Protection gaps are recorded during a focus session when the extension/bridge/rules are not verified. | AC-BRW-010: Gap duration appears in the session summary and does not silently become `Protected`. |
| BS-BRW-011 | MUST | Incognito access and unsupported browser surfaces are reported with explicit coverage states. | AC-BRW-011: The app reports incognito as `Allowed`, `NotAllowed`, or `Unverified` using browser-exposed extension APIs; browser-internal pages, guest profiles, and unsupported browsers are reported as `Unverified` or `Unsupported`; no status claims incognito coverage unless the extension is allowed in incognito and the heartbeat and rules are verified. |
| BS-BRW-012 | MUST | School-managed policy restrictions produce a graceful, visible state. | AC-BRW-012: When extension installation or Native Messaging is blocked, the app continues in the available mode and explains the limit. |
| BS-BRW-013 | MUST | The extension does not collect or transmit browsing history, page content, cookies, form data, or URLs. | AC-BRW-013: Extension source review and network capture show only rule/status messages needed for enforcement. |
| BS-BRW-014 | MUST | Strict with website rules cannot start until protection is verified. | AC-BRW-014: Start is disabled and the message states which condition is unmet — extension missing, bridge disconnected, rules unacknowledged, or heartbeat stale — with a direct recovery action; no session row is created until the user resolves the condition or changes the profile. |
| BS-BRW-015 | MUST | A blocked request shows an extension-hosted explanation page that names the session and the reason. | AC-BRW-015: The page appears for blocked HTTP/HTTPS requests and does not reveal browsing history. |
| BS-BRW-016 | SHOULD | Multiple supported browser windows and profiles receive the same session policy. | AC-BRW-016: Two Chrome windows and a second profile connected through the bridge apply the same active rules. |
| BS-BRW-017 | MUST | The extension clears or expires rules if the agent disappears. | AC-BRW-017: After the configured expiry, blocked sites are reachable again even if the agent is gone. |
| BS-BRW-018 | MUST | A connection test is available in Settings. | AC-BRW-018: Test reports browser, extension, bridge, heartbeat, and rule-application status separately. |
| BS-BRW-019 | MUST | The extension ID, store channel, and required permissions are documented before release. | AC-BRW-019: Release checklist contains the fixed ID, allowed origin, store listing, and permission justification. |

---

## 9. Study Manager (STU)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-STU-001 | MUST | Users can create, rename, describe, archive, and view subjects. | AC-STU-001: Archived subjects disappear from new-task selection but remain readable in history. |
| BS-STU-002 | MUST | Users can create, edit, prioritize, schedule a local due date, estimate, and add notes to tasks. | AC-STU-002: All fields persist and validate. |
| BS-STU-003 | MUST | Task title is required and limited to 200 characters; estimate is 5–600 minutes in 5-minute increments. | AC-STU-003: Invalid input is blocked with a field-level message. |
| BS-STU-004 | MUST | Task status supports Todo, InProgress, Done, and Archived with valid transitions. | AC-STU-004: Invalid transitions are rejected or normalized without corrupting history. |
| BS-STU-005 | MUST | The today view is manual; users can add/remove tasks from today without automatic scheduling. | AC-STU-005: The today list changes only through user action. |
| BS-STU-006 | MUST | A focus session can link to one task; the task and subject are stored on the session with snapshots. | AC-STU-006: Renaming or deleting the task does not break historical session display. |
| BS-STU-007 | MUST | Marking a task Done awards task XP once. Reopening and re-completing does not award again. | AC-STU-007: Ledger has exactly one task-completion entry for the task. |
| BS-STU-008 | MUST | Task deletion is soft; archived/deleted tasks are excluded from active lists but retained for history. | AC-STU-008: Deleted task history remains readable. |
| BS-STU-009 | SHOULD | Users can filter and sort tasks by status, subject, due date, priority, and today flag. | AC-STU-009: Filter combinations produce deterministic results. |
| BS-STU-010 | MUST | Empty, loading, validation, and database-error states are distinct. | AC-STU-010: No state displays a false zero or silently drops unsaved input. |
| BS-STU-011 | MUST | MVP does not auto-schedule tasks or claim AI planning. | AC-STU-011: No task receives a planned time without user action. |
| BS-STU-012 | SHOULD | Secondary task and subject workflows—filtering, sorting, bulk archive, and archive review—are keyboard operable and screen-reader labeled. | AC-STU-012: Keyboard-only filtering, sorting, bulk archive, and archive review pass; core creation, editing, and completion remain covered by BS-XCT-006. |

---

## 10. Gamification and Progression (GAM)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-GAM-001 | MUST | Session XP is awarded only for active-study time of at least 10 minutes, using the `ECONOMY-1` profile rate and outcome multiplier. | AC-GAM-001: Seeded sessions produce the exact `ECONOMY-1` expected XP for completed, ended-early, abandoned, and recovery cases. |
| BS-GAM-002 | MUST | Session XP is capped at 300 XP per completion local day; a final session may earn only the remaining balance, and no unused amount carries forward; task and achievement XP are separate. | AC-GAM-002: With 280 session XP already awarded, a 30-XP session awards 20; after 300, a fifth session awards 0; a task completion still awards 25. |
| BS-GAM-003 | MUST | `ECONOMY-1` levels use 500 XP per level, capped at level 50, with total XP continuing to record. | AC-GAM-003: Boundary tests at 0, 499, 500, 24,500, and above 25,000 XP match the `ECONOMY-1` formula. |
| BS-GAM-004 | MUST | Streak is based on qualified completed sessions and recorded local dates. | AC-GAM-004: Same-day sessions do not increment twice; a missed day resets the current streak. |
| BS-GAM-005 | MUST | The five MVP achievements unlock once and award the fixed `ECONOMY-1` XP values. | AC-GAM-005: Each achievement unlocks at its threshold, cannot duplicate, displays its criteria, and records the `ECONOMY-1` award. |
| BS-GAM-006 | MUST | All rewards use the append-only ledger with unique idempotency keys. | AC-GAM-006: Repeating the same completion, restarting, or replaying an event creates no duplicate entry. |
| BS-GAM-007 | MUST | Session completion and reward creation are atomic. | AC-GAM-007: A forced failure rolls back both; a retry succeeds once. |
| BS-GAM-008 | MUST | No XP is awarded for application launch, onboarding, dashboard visits, or settings changes. | AC-GAM-008: Repetition of those actions leaves ledger totals unchanged. |
| BS-GAM-009 | MUST | Task completion XP is 25 once per task and does not count against the session cap. | AC-GAM-009: Duplicate completion and delete/recreate actions do not create duplicate awards. |
| BS-GAM-010 | MUST | Level-up and achievement feedback is shown without interrupting an active focus session. | AC-GAM-010: Feedback is queued until the session reaches a terminal state and is then presented before the next session starts; it is never shown while a blocking overlay or confirmation countdown is active. |
| BS-GAM-011 | MUST | The MVP awards no tokens and displays no token balance, token spending, shop, or theme UI. | AC-GAM-011: No token balance, token award, purchase surface, or theme surface is reachable in the MVP. |
| BS-GAM-012 | MUST | The schema preserves future token entries across mode upgrades and migrations. | AC-GAM-012: A seeded `TOKEN` ledger entry survives Limited-to-Full upgrade, backup, and restore. |
| BS-GAM-013 | MUST | Every reward entry stores the economy rule version that generated it; MVP entries use `ECONOMY-1`, and diagnostics show the version. | AC-GAM-013: Changing to a later economy version does not recalculate historical entries; each entry remains explainable by its stored rule version. |
| BS-GAM-014 | MUST | Daily-cap and streak dates use the recorded local date at completion. | AC-GAM-014: Timezone-change and cross-midnight tests follow the domain rules. |
| BS-GAM-015 | MUST | Ledger entries cannot be edited or deleted through normal UI. | AC-GAM-015: No edit/delete path exists; database constraints protect idempotency. |
| BS-GAM-016 | MUST | Reward state is recoverable from the ledger after UI restart. | AC-GAM-016: Reopening the app shows the same XP, level, streak, and achievements. |
| BS-GAM-017 | SHOULD | The progression surface explains that local data can be modified and that rewards are motivational, not competitive. | AC-GAM-017: The explanation is available from achievements or settings. |
| BS-GAM-018 | MUST | An `AchievementCode` permanently identifies one eligibility definition. `CriteriaType` and `Threshold` must not change, be overwritten, or be deleted under an existing code; achievement definitions must not be silently overwritten or deleted; changing eligibility requires a new `AchievementCode`. | AC-GAM-018: A catalog audit finds one immutable eligibility definition per `AchievementCode` and no silent definition overwrite or deletion; an attempted criteria change, deletion, or code reuse is rejected; historical `UserAchievement` rows still resolve to the original definition. |

---

## 11. Insights (INS)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-INS-001 | MUST | Insights provides today, this week, and last 30 days. | AC-INS-001: Each range uses the defined local-day boundaries; subject totals sum to the range total; session XP totals reconcile with the reward ledger. |
| BS-INS-002 | MUST | Insights shows verified active-study time, session elapsed time, break time, completed sessions, ended-early sessions, and protection gaps. | AC-INS-002: Each metric is labeled and traceable to session/ledger data. |
| BS-INS-003 | MUST | Insights shows subject split based on completed/ended focus sessions. | AC-INS-003: Subject totals reconcile with session history for the same range. |
| BS-INS-004 | MUST | Session history lists date, task, subject, profile, planned duration, active time, outcome, protection status, and XP. | AC-INS-004: Selecting a row opens the matching summary. |
| BS-INS-005 | MUST | Metric definitions are available in the UI. | AC-INS-005: Each metric has a plain-language definition including idle and protection-gap treatment. |
| BS-INS-006 | MUST | Empty and insufficient-data states are clear. | AC-INS-006: Percentages are suppressed when the range contains fewer than 5 sessions; counts are shown instead. Subject-split percentages are suppressed when a subject has fewer than 3 sessions in the range; the UI states why the percentage is unavailable. |
| BS-INS-007 | MUST | Charts have accessible tables or equivalent text summaries. | AC-INS-007: All data is reachable without color or pointer-only interaction. |
| BS-INS-008 | MUST | MVP does not show a composite “productivity score” or social comparison. | AC-INS-008: No score, rank, leaderboard, or peer comparison appears. |
| BS-INS-009 | MUST | Insight totals reconcile with the ledger for XP and with sessions for time. | AC-INS-009: Automated reconciliation tests pass after recovery, migration, and timezone changes. |
| BS-INS-010 | SHOULD | Insights remains usable while long-running queries execute at 100,000 sessions. | AC-INS-010: The UI shows a progress state if a query exceeds 200 ms, remains keyboard-responsive, and the underlying query meets BS-NFR-PERF-008. |

---

## 12. Data Management (DAT)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-DAT-001 | MUST | The study database is stored at `%LocalAppData%\BoundlessStudy\boundless.db` by default. | AC-DAT-001: Both modes use the same path and schema. |
| BS-DAT-002 | MUST | SQLite runs with foreign keys and WAL mode. The per-user agent is the sole database owner and the only process that opens the database; the UI, service, bridge, and extension access data only through agent IPC. | AC-DAT-002: The UI submits read and write requests through the UI pipe; no UI/service/bridge/extension process opens the database. Concurrent agent-mediated reads use WAL snapshots and do not corrupt data; writes are serialized by the agent. |
| BS-DAT-003 | MUST | EF Core migrations are versioned and preceded by a backup when data can be altered. | AC-DAT-003: A failed migration leaves a readable pre-migration backup. |
| BS-DAT-004 | MUST | Automatic backups run on a defined schedule and retain the seven most recent plus the latest pre-migration backup. | AC-DAT-004: Retention and naming match the policy; old backups are removed only after a new verified backup exists. |
| BS-DAT-005 | MUST | Manual backup produces a manifest, schema version, app version, checksum, and database snapshot. | AC-DAT-005: Manifest and checksum validate on restore. |
| BS-DAT-006 | MUST | Restore validates the backup, creates a safety backup, verifies integrity, and handles failure without corrupting current data. | AC-DAT-006: A corrupted or incompatible backup is rejected; current data remains available. |
| BS-DAT-007 | MUST | Startup and backup paths perform an integrity check. | AC-DAT-007: `PRAGMA integrity_check` results are recorded; failure enters recovery state. |
| BS-DAT-008 | MUST | Corruption recovery offers restore or quarantine; it never silently deletes history. | AC-DAT-008: Corrupt database is quarantined with a diagnostic record. |
| BS-DAT-009 | MUST | Users can export subjects, tasks, sessions, rewards, and achievements in a documented UTF-8 JSON archive with a manifest. | AC-DAT-009: Export produces a ZIP containing `manifest.json`, `subjects.json`, `tasks.json`, `sessions.json`, `rewards.json`, and `achievements.json`; the manifest records schema, app, and economy versions, export time, record counts, and a SHA-256 checksum per file; each file validates against the documented schema and is readable without the app. |
| BS-DAT-010 | MUST | “Delete all data” stops sessions, requires explicit confirmation, and removes database, backups, logs, and local settings. | AC-DAT-010: After deletion, restarting creates a clean profile; no hidden copy remains in the app data directories. |
| BS-DAT-011 | MUST | Uninstall offers keep-data or delete-data and explains extension removal. | AC-DAT-011: Both choices are verified in Full and Limited modes. |
| BS-DAT-012 | MUST | Diagnostics export includes health, versions, logs, schema, and redacted error context without personal content or URLs. | AC-DAT-012: Inspection finds no browsing URLs, window titles, task notes, or names in the bundle. |
| BS-DAT-013 | MUST | Logs rotate and do not contain browsing history, window titles, keystrokes, or document content. | AC-DAT-013: Log inspection passes the privacy checklist. |
| BS-DAT-014 | MUST | Database, backup, and log directories are protected by user-scoped ACLs. | AC-DAT-014: A different standard user cannot read or write the files. |
| BS-DAT-015 | MUST | Schema version is checked before UI connection and after restore. | AC-DAT-015: Mismatch produces a recovery/update state, not a partial load. |
| BS-DAT-016 | MUST | Limited-to-Full upgrade preserves the same database and profile. | AC-DAT-016: After upgrade, the same profile ID and database path are present; row counts per table, primary keys, non-derived field values, relationships, ledger idempotency keys, and derived XP/level/streak totals match the pre-upgrade seeded dataset. |
| BS-DAT-017 | MUST | Any future token data is preserved by migration and mode upgrade. | AC-DAT-017: Seeded token ledger entries survive upgrade, backup, restore, and migration. |
| BS-DAT-018 | MUST | No cloud backup or synchronization is used in MVP. | AC-DAT-018: No network call carries study data. |

---

## 13. Notifications (NOT)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-NOT-001 | MUST | A notification is produced when a focus session completes or ends early. | AC-NOT-001: Notification reports outcome and active time without blocking the summary. |
| BS-NOT-002 | MUST | A notification is produced when a break ends. | AC-NOT-002: Notification occurs once and respects preferences. |
| BS-NOT-003 | MUST | Protection loss, agent/service failure, and overlay failure produce a persistent in-app banner at the top of the current screen, with severity text and a recovery action, and a system notification when the chosen packaging supports it. | AC-NOT-003: The banner identifies the affected surface and recovery action; it remains until the condition is resolved, or is dismissible only for non-critical categories; critical protection failures remain visible until resolved or the session ends. |
| BS-NOT-004 | MUST | Achievement and level-up feedback is delivered at a safe moment. | AC-NOT-004: It is not delivered over the blocking overlay or during a confirmation countdown. |
| BS-NOT-005 | MUST | Backup failure produces a visible warning until resolved or dismissed. | AC-NOT-005: Backup status remains unhealthy in Settings. |
| BS-NOT-006 | SHOULD | Users can configure per-category quiet-hour overrides beyond the basic notification controls required by BS-SET-004. | AC-NOT-006: A quiet-hour override suppresses only that category's system notification for the configured window; critical protection failures remain visible in-app. |
| BS-NOT-007 | MUST | Notifications are rate-limited and not used for engagement spam. | AC-NOT-007: Repeated starts/stops do not produce duplicate or nagging notifications. |
| BS-NOT-008 | MUST | Notification controls are keyboard and screen-reader accessible. | AC-NOT-008: All settings and notices can be reached and dismissed without a pointer. |
| BS-NOT-009 | MUST | Toast registration and AppUserModelID requirements are verified for the chosen packaging. | AC-NOT-009: Session-end and protection-loss notifications appear in Full and Limited installations. |
| BS-NOT-010 | MUST | The MVP has no persistent notification inbox or advanced Notification Center. | AC-NOT-010: No inbox, unread center, or rich notification history is exposed. |

---

## 14. System Integration (SYS)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-SYS-001 | MUST | Setup detects permissions and capabilities before mode selection. | AC-SYS-001: Diagnostics distinguish elevation, admin eligibility, service availability, browser policy, and extension availability. |
| BS-SYS-002 | MUST | Full mode installs the Windows service and requires administrator privileges only for privileged operations. | AC-SYS-002: A clean Full install succeeds after one elevation; standard runtime does not require elevation. |
| BS-SYS-003 | MUST | Limited mode installs and runs without administrator rights and without a Windows service. | AC-SYS-003: A standard-user clean install, session, update, and uninstall succeed without a service or elevation prompt. |
| BS-SYS-004 | MUST | The Windows service runs independently, starts at boot, supervises the agent, and records health. | AC-SYS-004: Service start, stop, restart, and health states are visible in diagnostics. |
| BS-SYS-005 | MUST | The service does not write user study data, display interactive UI, terminate arbitrary processes, or install hidden persistence. | AC-SYS-005: Code and runtime inspection confirm the boundary. |
| BS-SYS-006 | MUST | The per-user agent runs in the user session, owns session state and the database, and serves UI and bridge clients. | AC-SYS-006: Closing the UI does not stop an active session; the agent is the sole database owner and the only process that opens it, for reads and writes. |
| BS-SYS-007 | MUST | The agent enforces one instance per Windows user. | AC-SYS-007: A second agent launch exits or hands off to the existing instance. |
| BS-SYS-008 | MUST | Shared application functionality does not depend on the privileged service. | AC-SYS-008: Limited mode passes the core-loop acceptance suite without service binaries installed. |
| BS-SYS-009 | MUST | Full mode starts the agent at user logon and attempts supervised recovery when it stops unexpectedly. | AC-SYS-009: Service restart behavior is tested at logon, logoff, crash, and fast user switching; failure produces degraded status. |
| BS-SYS-010 | MUST | Limited mode starts the agent through a documented per-user startup mechanism without elevation. | AC-SYS-010: Disabling startup is visible and reversible in Settings. |
| BS-SYS-011 | MUST | UI-to-agent communication uses a versioned named-pipe protocol with request IDs, timeouts, and size limits. | AC-SYS-011: Malformed, oversized, and slow requests are rejected without crashing the agent. |
| BS-SYS-012 | MUST | The native-messaging bridge is stateless and connects the extension only to the local agent. | AC-SYS-012: Bridge restart reconnects; no study data is stored in the bridge. |
| BS-SYS-013 | MUST | Pipes use user-scoped ACLs and validate the connecting user and role. | AC-SYS-013: A different standard user and an unapproved client are rejected. |
| BS-SYS-014 | MUST | IPC and Native Messaging schemas are versioned, validated, and bounded. | AC-SYS-014: Unknown fields/types and incompatible versions produce a protocol error, not a mutation. |
| BS-SYS-015 | MUST | Native Messaging registration uses the fixed extension ID, allowed origin, and documented HKCU registration. | AC-SYS-015: Chrome and Edge launch the expected host; a mismatched origin fails safely. |
| BS-SYS-016 | MUST | Startup order handles service, agent, UI, and browser in any order. | AC-SYS-016: Opening the UI before the agent, or the browser before the agent, reconnects without data loss. |
| BS-SYS-017 | MUST | Lock, sleep, resume, shutdown, and reboot are handled per the domain rules. | AC-SYS-017: No downtime is credited; a session with a valid checkpoint returns to `RecoverableInterrupted`; an unrecoverable session becomes `Abandoned` with its last checkpointed active time. |
| BS-SYS-018 | MUST | Multiple Windows users each have an isolated agent, database, pipe, and session. | AC-SYS-018: Fast user switching cannot expose or merge another user’s data. |
| BS-SYS-019 | MUST | Agent crash or application crash does not duplicate sessions or rewards. | AC-SYS-019: Recovery tests show one session and one ledger entry. |
| BS-SYS-020 | MUST | Limited-to-Full upgrade installs privileged components and preserves data and profile identity. | AC-SYS-020: Upgrade test compares logical data before and after; no second profile or reset occurs. |
| BS-SYS-021 | MUST | A failed or declined upgrade leaves Limited mode usable with no data change. | AC-SYS-021: Canceling UAC or forcing installer failure preserves the original state. |
| BS-SYS-022 | MUST | Mode and component health are visible and never inferred from a single “running” flag. | AC-SYS-022: Service, agent, bridge, extension, and rules each have distinct health states. |
| BS-SYS-023 | MUST | Stopping the service in Full mode produces a visible degraded-supervision state. | AC-SYS-023: Existing sessions remain truthful; next UI interaction reports degraded supervision. |
| BS-SYS-024 | MUST | Uninstall removes service, autostart, pipes, and privileged files in Full mode; Limited mode removes per-user startup and app files. | AC-SYS-024: Post-uninstall inspection finds no hidden service, startup task, or pipe. |
| BS-SYS-025 | MUST | Diagnostics export includes versions, mode, health, extension status, service status, logs, and schema without personal content. | AC-SYS-025: Bundle is actionable and privacy-checked. |
| BS-SYS-026 | MUST | Structured local logs rotate and correlate session, IPC, browser, and service events without sensitive content. | AC-SYS-026: Log review passes the privacy checklist; for a seeded failed request, support can reconstruct the failing component and action from timestamp, correlation ID, and event sequence without personal content. |
| BS-SYS-027 | MUST | Core operation makes no network requests. | AC-SYS-027: Network-disabled acceptance suite passes. |
| BS-SYS-028 | MUST | No kernel driver, global injection, or undocumented Windows modification is used. | AC-SYS-028: Install and runtime inspection confirms the boundary. |
| BS-SYS-029 | MUST | Release binaries, installer, and native host are code-signed and versioned. | AC-SYS-029: Signature verification passes on a clean test machine. |
| BS-SYS-030 | MUST | Limited mode does not repeatedly request elevation. | AC-SYS-030: A standard user can complete all non-upgrade workflows with zero prompts. |
| BS-SYS-031 | MUST | Browser policy failures are surfaced without disabling unrelated features. | AC-SYS-031: A policy-blocked extension leaves tasks, timers, XP, and insights functional. |
| BS-SYS-032 | MUST | Full and Limited modes use the same local account, data path, and schema. | AC-SYS-032: A database created in either mode opens in the other with the same profile. |
| BS-SYS-033 | MUST | The capability matrix is available in setup, Settings, and user documentation. | AC-SYS-033: Users can compare modes before and after installation. |
| BS-SYS-034 | MUST | Mode status is displayed without claiming stronger enforcement than is active. | AC-SYS-034: Full with stopped service and Limited both show accurate supervision and enforcement status. |
| BS-SYS-035 | MUST | Service-supervised agent restart is attempted only through documented, removable mechanisms. | AC-SYS-035: Restart uses registered components and leaves no hidden persistence. |

---

## 15. Settings and Accessibility (SET)

| ID | Pri | Requirement | Acceptance criteria |
|---|---|---|---|
| BS-SET-001 | MUST | Users can select the default profile and default focus/break durations. | AC-SET-001: New sessions use the defaults; changing defaults does not rewrite history. |
| BS-SET-002 | MUST | Users can manage blocked applications and blocked sites. | AC-SET-002: Changes persist and apply to the next session. |
| BS-SET-003 | MUST | Users can manage explicit allowed sites and see precedence. | AC-SET-003: Allow rules override matching block rules and are explained in the UI. |
| BS-SET-004 | MUST | Users can enable or disable non-critical notification categories, set one global quiet-hours window, and control session, break, and backup notifications. | AC-SET-004: Preferences persist and apply to the next notification; disabled non-critical categories produce no system notification; global quiet hours suppress non-critical system notifications; critical protection failures remain visible in-app; session and break notifications still follow the outcome rules in BS-NOT-001 and BS-NOT-002. |
| BS-SET-005 | MUST | Users can enable or disable per-user startup in Limited mode and view Full-mode service startup. | AC-SET-005: Changes are transparent and reversible. |
| BS-SET-006 | MUST | Data & Backup provides backup, restore, export, integrity check, location, and deletion. | AC-SET-006: Each action reports outcome and does not silently overwrite data. |
| BS-SET-007 | MUST | Accessibility preferences inherit Windows high contrast, text scaling/DPI, and reduced-motion settings, and expose app-specific focus visibility, timer-announcement, and chart-table options. | AC-SET-007: High contrast, 100–200% scaling, keyboard, Narrator, and reduced-motion tests pass; changing the corresponding Windows setting changes the app behavior; any unsupported Windows setting is documented with its fallback. |
| BS-SET-008 | MUST | Diagnostics shows mode, versions, agent, service, bridge, extension, database, backup, and last error. | AC-SET-008: Rerunning diagnostics updates all components. |
| BS-SET-009 | MUST | About shows product version, schema version, privacy summary, licenses, and support/export path. | AC-SET-009: Values match the installed build. |
| BS-SET-010 | MUST | Installation Mode shows Full or Limited, capability differences, health, and the upgrade path. | AC-SET-010: Upgrade is offered only when eligible; denial leaves Limited mode intact. |
| BS-SET-011 | MUST | Settings provides recovery, uninstall, and extension-removal guidance. | AC-SET-011: Instructions are accurate for the current mode. |
| BS-SET-012 | MUST | Changing blocklists during an active session applies to the next session, except temporary exceptions. | AC-SET-012: Mid-session edits do not silently weaken an active Strict session. |
| BS-SET-013 | SHOULD | Users can rerun onboarding diagnostics or reset onboarding without deleting study data. | AC-SET-013: Reset affects onboarding flags only, not tasks, sessions, or ledger. |
| BS-SET-014 | MUST | Settings does not expose visual theme or color customization until references and a later release approve it. | AC-SET-014: No invented theme controls exist in MVP. |
| BS-SET-015 | MUST | Browser connection test reports extension, bridge, heartbeat, and rule status separately. | AC-SET-015: Each failure has a recovery action. |
| BS-SET-016 | MUST | Full-mode service status and Limited-mode absence are explained without alarming or misleading copy. | AC-SET-016: In a moderated comprehension test with at least 5 non-developer participants, at least 4 correctly answer three questions: what mode is active, what supervision is present, and what still works if the service is unavailable. |

---

## 16. Acceptance summary

The MVP functional baseline is accepted only when:

1. Every MUST requirement has a passing test or a documented, user-approved exception.
2. Full and Limited modes are tested independently.
3. Limited-to-Full upgrade and data preservation are tested with all MVP data types and seeded token entries.
4. Every protection status is backed by a real health check.
5. No visual acceptance is claimed without references.
6. Test evidence is linked from `../05-quality/test-strategy.md`.
