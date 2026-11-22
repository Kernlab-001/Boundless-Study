# UI/UX Specification

**Product:** Boundless Study  
**Status:** Draft for user review  
**Version:** 0.2  
**Last updated:** 2026-11-12  
**Visual status:** Functional specification only. Visual details are blocked pending supplied references; functional implementation may proceed under the functionality-first boundary.  
**Related:** `functional-requirements.md`, `../01-product/product-requirements.md`, `ui-reference-gap-log.md`

## 1. Scope and visual boundary

This document defines:

- Information architecture and navigation.
- User journeys and workflows.
- Screen inventory and purposes.
- Information hierarchy and content requirements.
- Controls, interactions, functional states, validation, and recovery.
- Accessibility behavior.
- Asset locations and purposes.

This document does **not** define:

- Colors, typography, spacing, iconography, illustration style, motion style, window chrome, or final layout.
- Themes or visual customization.
- Replacement icons, emoji, or generated illustrations.
- A visual identity borrowed from gstack, unslop, or any other product.

Every screen and visual component is marked `BLOCKED — AWAITING USER REFERENCES` in `ui-reference-gap-log.md` until a supplied reference is inspected and approved.

**Functional-first boundary.** This specification may be implemented as a basic functional interface during functionality-first development. That interface is for operating and testing the product and is not final visual design. Visual refinement belongs to the dedicated UI phase after sufficient functional implementation and integration, and before final visual acceptance and release stabilization. The `BLOCKED` status blocks final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing. It does not block functional requirements, technical validation, business logic or database implementation, functional navigation, temporary testing interfaces, or feature-level automated testing.

## 2. Terminology and tone

### 2.1 User-facing terms

| Term | UI usage |
|---|---|
| Focus session | A timed study block. |
| Break | A separate rest block after a focus session. |
| Active-study time | Time counted as active study after idle and interruption rules. |
| Session elapsed time | Time the focus session has progressed, excluding lock, sleep, and overlay pauses. |
| Protection | A verified blocking surface. Never used as a generic promise. |
| Protected | Only when the specific surface is verified active. |
| Best effort | Limited-mode and general enforcement description. |
| Application target — Supported | The target is a user-session executable and the agent can detect it and show the overlay at its integrity level. |
| Application target — Unsupported | The target is elevated, protected, fullscreen-exclusive, in another Windows session, or otherwise outside the agent’s supported coverage. |
| Application target — Unverified | The capability check has not completed or could not be completed. |
| Blocked application | An application the user selected for blocking. |
| Blocked site | A domain the user selected for blocking. |
| Allowed site | An explicit exception that overrides a matching blocked site. |
| Full mode | Installed with service supervision and administrator installation. |
| Limited mode | Per-user installation without a Windows service. |
| Qualified session | A completed focus session with at least 10 active-study minutes. |
| Recovery | Restoring a checkpointed session after interruption. |

Avoid:

- “Failed” for a person who ends a session early. Use “Ended early.”
- “On” for protection. Use the specific status.
- “Guaranteed,” “unbreakable,” “tamper-proof,” “anti-cheat,” or “cannot be bypassed.”
- Shame, streak threats, or loss-framing copy.
- Claims that a screen is protected when only one surface is protected.

### 2.2 Tone

Direct, calm, and factual. Explain what happened, what is still working, and what the user can do next. Do not moralize. Do not hide limitations in small text or release notes.

## 3. Information architecture

### 3.1 Primary destinations

| Destination | Purpose |
|---|---|
| Today | Today’s study state, planned tasks, quick start, protection health, recent activity. |
| Plan | Subjects, tasks, today planning, task details. |
| Focus | Session setup, pre-session summary, active session, break, summary, recovery. |
| Progress | Insights, session history, XP, level, streak, achievements. |
| Settings | Profiles, blocklists, notifications, data, installation mode, diagnostics, about. |

The exact navigation placement, visual grouping, and responsive behavior are blocked pending references. The destination set is functional.

### 3.2 Secondary surfaces

- First-run onboarding flow.
- Blocking overlay.
- System tray menu.
- Session recovery notices.
- Browser connection test.
- Diagnostics export.
- Installation-mode upgrade.
- Error and recovery dialogs.

### 3.3 Navigation rules

- Onboarding is a guided sequence; the user can resume after closing the app.
- After onboarding, primary destinations are always reachable unless an active blocking overlay is present.
- The blocking overlay is a focused surface with “Return to study” and “End session.” It must not trap the user without an exit.
- A notification can deep-link to the relevant session summary, protection status, backup status, or settings page.
- Closing the main window does not end an active session. The agent continues; the tray indicates active state.
- The exact visual shell, menu placement, and window behavior are blocked pending references.

## 4. Screen inventory

| ID | Screen | Purpose | Key requirements | Visual status |
|---|---|---|---|---|
| SCR-001 | Welcome and privacy | Explain local-first data, no account, and what will be installed. | ONB-001, XCT-001, XCT-002 | BLOCKED — AWAITING USER REFERENCES |
| SCR-002 | Environment check | Show detected permissions, mode eligibility, browsers, existing data, and policy restrictions. | ONB-002, SYS-001 | BLOCKED — AWAITING USER REFERENCES |
| SCR-003 | Mode comparison and selection | Explain Full versus Limited before choice. | ONB-003, ONB-004, SYS-033 | BLOCKED — AWAITING USER REFERENCES |
| SCR-004 | Full-mode permission step | Explain service, elevation, supervision, recovery, and uninstall. | ONB-005, SYS-002, SYS-004 | BLOCKED — AWAITING USER REFERENCES |
| SCR-005 | Limited-mode confirmation | State what works, what is weaker, and how to upgrade later. | ONB-004, ONB-006, SYS-003 | BLOCKED — AWAITING USER REFERENCES |
| SCR-006 | Browser setup | Guide extension installation and connection, or explain policy block. | ONB-007, ONB-008, BRW-002, BRW-012 | BLOCKED — AWAITING USER REFERENCES |
| SCR-007 | First subject and task | Create a starting task or skip. | ONB-009, STU-001, STU-002 | BLOCKED — AWAITING USER REFERENCES |
| SCR-008 | First-session tutorial | Explain blocking, limits, early exit, recovery, and uninstall. | ONB-010 | BLOCKED — AWAITING USER REFERENCES |
| SCR-009 | Today | Dashboard and quick start. | DSH-001–007 | BLOCKED — AWAITING USER REFERENCES |
| SCR-010 | Focus setup | Select profile, duration, task, and review blockers. | FOC-001, FOC-002 | BLOCKED — AWAITING USER REFERENCES |
| SCR-011 | Pre-session summary | Disclose protection status, application target coverage, and limits before start. | FOC-002, BRW-010, APP-010, APP-012 | BLOCKED — AWAITING USER REFERENCES |
| SCR-012 | Active focus | Show remaining time, active status, protection, and session controls. | FOC-004–007, FOC-017, APP-010 | BLOCKED — AWAITING USER REFERENCES |
| SCR-013 | Blocking overlay | Interrupt a blocked application with return/end actions. | APP-004–006, APP-011, APP-012 | BLOCKED — AWAITING USER REFERENCES |
| SCR-014 | Break | Break timer and return-to-focus path. | FOC-008, NOT-002 | BLOCKED — AWAITING USER REFERENCES |
| SCR-015 | Session summary | Show outcomes, metrics, protection, violations, XP, and next actions. | FOC-012, GAM-001–010, INS-005 | BLOCKED — AWAITING USER REFERENCES |
| SCR-016 | Session recovery | Explain interruption, checkpoint, resume/abandon choice, and downtime. | FOC-011, SYS-019 | BLOCKED — AWAITING USER REFERENCES |
| SCR-017 | Plan / tasks | Browse, filter, and manage tasks. | STU-001–012 | BLOCKED — AWAITING USER REFERENCES |
| SCR-018 | Task editor | Create or edit a task. | STU-002–004 | BLOCKED — AWAITING USER REFERENCES |
| SCR-019 | Subjects | Manage subjects and archives. | STU-001, STU-008 | BLOCKED — AWAITING USER REFERENCES |
| SCR-020 | Progress / insights | Daily/weekly/30-day metrics and subject split. | INS-001–010 | BLOCKED — AWAITING USER REFERENCES |
| SCR-021 | Session history | Browse past sessions and reopen summaries. | INS-004, FOC-018 | BLOCKED — AWAITING USER REFERENCES |
| SCR-022 | Achievements | Show earned and unearned achievements, levels, and streak. | GAM-003–005, GAM-010 | BLOCKED — AWAITING USER REFERENCES |
| SCR-023 | Settings home | Organize settings and health surfaces. | SET-001–016 | BLOCKED — AWAITING USER REFERENCES |
| SCR-024 | Focus profiles and defaults | Choose default profile and durations. | SET-001, FOC-003 | BLOCKED — AWAITING USER REFERENCES |
| SCR-025 | Blocked applications | Manage application targets. | APP-001–003 | BLOCKED — AWAITING USER REFERENCES |
| SCR-026 | Blocked and allowed sites | Manage domains and precedence. | BRW-006–007, SET-002–003 | BLOCKED — AWAITING USER REFERENCES |
| SCR-027 | Notifications | Configure categories and quiet hours. | NOT-006–008, SET-004 | BLOCKED — AWAITING USER REFERENCES |
| SCR-028 | Data and backup | Backup, restore, export, integrity, deletion. | DAT-004–012 | BLOCKED — AWAITING USER REFERENCES |
| SCR-029 | Installation mode and upgrade | Explain mode, show health, offer upgrade. | SYS-020–023, SET-010 | BLOCKED — AWAITING USER REFERENCES |
| SCR-030 | Diagnostics | Health, versions, connection status, logs, export. | SYS-022–026, SET-008 | BLOCKED — AWAITING USER REFERENCES |
| SCR-031 | Recovery and uninstall guide | Explain session recovery, component removal, data choice, and extension removal. | ONB-010, DAT-011, SYS-024 | BLOCKED — AWAITING USER REFERENCES |
| SCR-032 | About and privacy | Version, schema, privacy summary, licenses, support. | SET-009 | BLOCKED — AWAITING USER REFERENCES |
| SCR-033 | Browser connection test | Test browser, extension, bridge, heartbeat, and rules separately. | BRW-018, SET-015 | BLOCKED — AWAITING USER REFERENCES |
| SCR-034 | Error and degraded states | Reusable error, unavailable, blocked-by-policy, database, and upgrade-failure states. | XCT-007, SYS-022 | BLOCKED — AWAITING USER REFERENCES |
| SCR-035 | Tray menu | Open app, view active-session status, open recovery, exit agent with warning. | FOC-017, SYS-006 | BLOCKED — AWAITING USER REFERENCES |

## 5. Core user flows

### F-01 — First run, Limited mode

1. User launches the app.
2. Welcome explains local data location, no account, no telemetry, and install scope.
3. Environment check shows Windows version, standard-user status, existing data, browsers, extension state, and policy restrictions.
4. Mode comparison explains Full and Limited.
5. Limited is available; user selects it.
6. App confirms no service will be installed and no administrator prompt will appear.
7. Browser setup attempts detection. If installation is permitted, user is guided. If policy-blocked, app records `Unavailable`.
8. User creates or skips the first subject/task.
9. First-session tutorial explains best-effort enforcement, overlay, application target coverage, early exit, recovery, and uninstall.
10. User completes onboarding and reaches Today.

Alternate:

- Existing data is detected: offer use existing data, restore, or create new. Never silently overwrite.
- User cancels: no privileged changes; app can resume onboarding.

### F-02 — First run, Full mode

1. User selects Full mode after the comparison.
2. Permission step explains service, elevation, supervision, agent recovery, native host, data location, and uninstall.
3. User confirms; one elevation request occurs.
4. Installer installs service and per-machine components, registers native host and per-user launch task, and starts the service.
5. App verifies service, agent, pipe, database, and extension status.
6. Full mode is shown only after health checks pass.
7. User continues with browser setup and onboarding.

Alternate:

- Elevation denied: return to mode selection or continue Limited.
- Install failure: preserve existing data, show diagnostics, no false Full status.

### F-03 — Browser policy blocks extension

1. Browser setup detects that installation or Native Messaging is blocked.
2. App explains the exact surface unavailable: browser rules, not the whole product.
3. The user can continue with tasks, focus timer, XP, and insights.
4. Website protection status is `Unavailable`.
5. Strict with site rules cannot start; the user can remove site rules or choose another profile.
6. Onboarding completes with the limitation recorded.

### F-04 — Plan a task and start focus

1. User opens Plan or Today.
2. Creates a task with title, optional subject, due date, estimate, and priority.
3. Adds the task to Today.
4. Selects Start focus.
5. Focus setup prefilled with task, default profile, and duration.
6. User selects profile and duration.
7. Pre-session summary shows blocked apps/sites, browser status, exceptions policy, and limits.
8. User starts.
9. Active focus shows remaining time, active-study time, protection status, and end-session control.

Validation:

- Empty title blocks save.
- Invalid estimate blocks save.
- No profile/duration blocks start.
- Strict with site rules and unavailable browser blocks start with a clear reason.

### F-05 — Blocked application violation

1. While active, a blocked application becomes foreground.
2. For 3 seconds, no overlay.
3. If it remains foreground, the overlay appears and the session enters `Blocked`.
4. The overlay names the application and shows remaining focus time.
5. The overlay offers “Return to study” and “End session.”
6. Timer and active-study time remain paused.
7. The user returns; the overlay closes; tracking resumes.
8. The violation is added to the summary.

Error:

- Overlay cannot display: session pauses, enforcement failure is recorded, and the user is notified. No blocked time is credited.
- Target is `Unsupported` (elevated, protected, fullscreen-exclusive, another session, or secure desktop): the pre-session summary already disclosed the limitation; the active session never claims `Protected` for that target; if detection or overlay fails, the enforcement-failure path applies.

### F-06 — Browser protection lost

1. Extension heartbeat stops.
2. Within 30 seconds, protection status becomes `Degraded`.
3. A persistent, accessible notice explains that website rules are not verified.
4. The focus session continues; active-study time continues unless idle.
5. A protection gap is recorded.
6. The user can reconnect, open the connection test, or end the session.
7. Summary shows the gap duration.

### F-07 — Early exit

1. User selects End session.
2. Flexible ends immediately after confirmation-free action.
3. Balanced shows a confirmation and 10-second cooldown.
4. Strict requires the displayed confirmation phrase and a 30-second countdown.
5. Blocking is lifted when the session ends.
6. Summary shows active time, outcome, and partial XP if eligible.

The overlay and active session use the same end flow.

### F-08 — Break

1. After a focus session ends, the summary offers a break.
2. User selects a 1–60 minute break; default 5.
3. Break starts; blocking is lifted.
4. Break screen shows remaining time and end-break control.
5. System lock/sleep pauses the break.
6. Break completion notification appears.
7. User returns to Today or starts another focus session.

A break cannot start during an active focus session.

### F-09 — Completion and reward

1. Focus elapsed time reaches the plan.
2. Session completes; blocking is lifted.
3. Summary shows planned, elapsed, active, idle, protection gaps, violations, profile, and XP.
4. XP is awarded once.
5. Achievement or level feedback is queued until the session reaches a terminal state and presented before the next session starts; it is never shown over the blocking overlay or during a confirmation countdown.
6. User can view insights, start a break, or return to Today.

### F-10 — Crash recovery

1. Agent or computer restarts during a session.
2. Agent loads the checkpoint.
3. No downtime is credited.
4. A recovery notice explains the interruption and restored time.
5. Restrictions are reapplied if the session resumes.
6. User can resume or abandon.
7. If checkpoint is invalid or older than 24 hours, the session is abandoned with an explanation.

### F-11 — Limited to Full upgrade

1. Settings → Installation Mode shows Limited, capabilities, and a single upgrade action.
2. Upgrade explanation lists service, elevation, supervision, data preservation, and failure behavior.
3. User confirms.
4. One elevation request occurs.
5. Installer adds privileged components and restarts the agent.
6. Health checks pass; Full mode is shown.
7. Data and profile remain identical.
8. If elevation is denied or install fails, Limited mode remains active with no data change.

### F-12 — Backup and restore

1. Data & Backup shows last backup, next scheduled backup, location, and health.
2. Manual backup produces a manifest and checksum.
3. Restore requires selecting a valid backup and confirmation.
4. App creates a safety backup, stops writes, restores, verifies integrity, and restarts the agent.
5. Failure leaves existing data intact.
6. Exported files are clearly local.

### F-13 — Uninstall and recovery

1. Settings or installer explains keep-data versus delete-data.
2. Full mode removes the service, per-user launch task, native host, pipes, and privileged files.
3. Limited mode removes per-user startup and binaries.
4. App explains that the browser extension must be removed from the browser.
5. If data is kept, a later reinstall detects it.
6. If data is deleted, confirmation is explicit and irreversible.
7. Recovery from a disabled service or extension is documented.

## 6. Screen details

### 6.1 Today (SCR-009)

**Information hierarchy**

1. Current protection and mode state.
2. Today’s active-study time, XP, level, and streak.
3. Today’s planned tasks and next action.
4. Quick start.
5. Recent sessions.
6. Secondary insights link.

**Controls**

- Start focus.
- Open task.
- Add task.
- Open protection details.
- Open installation mode/health.
- Open recent session summary.

**States**

- Empty first run.
- No tasks planned.
- Active session in another window.
- Agent unavailable.
- Service degraded in Full mode.
- Browser protection unavailable.
- Loading.
- Database error.
- Backup unhealthy.

**Acceptance**

- All values are labeled and match domain calculations.
- No protection claim is made from partial health.
- Keyboard and screen-reader access to all actions.

### 6.2 Focus setup (SCR-010) and pre-session summary (SCR-011)

**Information hierarchy**

1. Task and subject.
2. Profile.
3. Duration.
4. Blocked applications/sites summary.
5. Browser protection status.
6. Exceptions policy.
7. Enforcement limits.
8. Start action.

**Controls**

- Select task.
- Select profile.
- Select duration.
- Manage blocklists.
- Start.
- Cancel.

**Validation**

- Strict with site rules requires browser protection.
- No active session.
- Duration within range.
- No blocked targets configured produces a warning, not a protection claim.
- Blocked application targets are listed as `Supported`, `Unsupported`, or `Unverified`. The summary explains that elevated, protected, fullscreen-exclusive, other-session, and secure-desktop targets are outside best-effort coverage. An unsupported target does not block session start unless the selected profile requires that surface; the limitation is disclosed before start.

### 6.3 Active focus (SCR-012)

**Information hierarchy**

1. Remaining session time.
2. Active-study time and protection status.
3. Current task/subject.
4. Session state: active, blocked, system paused, recovered.
5. End-session action.
6. Temporary exception action when allowed.

**Controls**

- End session.
- Grant temporary exception.
- Open protection details.
- Return from overlay.

No user-initiated pause is exposed in MVP.

**States**

- Active.
- Blocked.
- System paused.
- Recovered.
- Browser degraded/unavailable.
- Overlay failure.
- Service degraded in Full mode.

### 6.4 Blocking overlay (SCR-013)

**Information hierarchy**

1. What is blocked.
2. Remaining focus time.
3. Why it is blocked.
4. Return to study.
5. End session.
6. Temporary exception, if allowed by profile.

**Behavior**

- Topmost, keyboard accessible, on the active monitor.
- Timer and active study are paused.
- Escape does not silently bypass the block. It may focus “Return to study.”
- End session follows profile friction.
- The user can always reach the end-session action.
- No process termination.

### 6.5 Session summary (SCR-015)

**Information hierarchy**

1. Outcome and profile.
2. Planned versus elapsed versus active-study time.
3. Idle and protection gaps.
4. Violations and exceptions.
5. XP, level, streak, achievements.
6. Start break, start another session, view insights, return to Today.

**States**

- Completed.
- Ended early.
- Abandoned/recovered.
- XP capped.
- Achievement queued.
- Partial data after recovery.

### 6.6 Session recovery (SCR-016)

**Information hierarchy**

1. What happened.
2. Checkpointed elapsed/active time.
3. Downtime not credited.
4. Current protection status.
5. Resume or abandon.
6. Diagnostics link.

**Behavior**

- Resuming reapplies restrictions.
- Abandoning closes the session and awards partial XP if eligible.
- No silent resume without a notice.

### 6.7 Plan (SCR-017), task editor (SCR-018), subjects (SCR-019)

**Task list content**

- Task title, subject, due date, estimate, priority, status, today flag.
- Filters and sort controls.
- Empty state.
- Archived view.

**Task editor**

- Title, subject, due date, estimate, priority, status, notes.
- Save/cancel/archive.
- Field-level validation.
- No recurring tasks or subtasks in MVP.

**Subject management**

- Name, description, archive.
- Archived subjects remain in history.

### 6.8 Progress and insights (SCR-020, SCR-021, SCR-022)

**Information hierarchy**

1. Date range.
2. Active-study time and session outcomes.
3. Subject split.
4. Protection gaps and violations.
5. Session history.
6. XP, level, streak, achievements.

**States**

- No data.
- Insufficient data for percentages.
- Filtered empty.
- Large history pagination/virtualization.
- Export link.

**Acceptance**

- Every chart has an accessible table or text summary.
- Definitions are reachable from the metric label.
- No composite productivity score.

### 6.9 Settings (SCR-023–034)

Settings groups:

- Focus profiles and defaults.
- Blocked applications.
- Blocked and allowed sites.
- Notifications.
- Startup.
- Data and backup.
- Installation mode and upgrade.
- Diagnostics.
- Recovery and uninstall.
- About and privacy.

Rules:

- Changes to blocklists during a session apply to the next session.
- Installation mode is live health plus installed capability, not a static label.
- Limited mode shows a single, non-repeating upgrade path.
- Visual theme and customization are absent until a later release.

## 7. Global states and status language

| State | User-facing meaning | Required treatment |
|---|---|---|
| `Protected` | The specific surface is verified active. | State the surface, e.g. “Website rules applied in Chrome.” |
| `Degraded` | Protection was active but is not verified now. | Persistent warning; record gap; recovery action. |
| `Unavailable` | Required component absent or blocked. | Explain what still works and what does not. |
| `NotConfigured` | No rules exist. | Do not imply protection. Offer configuration. |
| `NotRequired` | No rules apply to this profile. | Explain that no blocking is configured for this session. |
| `Unknown` | Check not complete. | Loading/checking state; no claim. |
| Full — supervised | Service is healthy and agent supervision is active. | Do not imply tamper-proof or system-wide application coverage. |
| Full — degraded | Service missing/stopped/restart failed. | Explain that supervision is weaker. |
| Limited — best effort | No service; per-user agent only. | Explain that application coverage is limited to supported user-session targets; no system-wide or tamper-proof claim. |
| Application target — `Supported` | Agent can detect the user-session target and show the overlay at its integrity level. | Show the target state before the session. |
| Application target — `Unsupported` | Elevated, protected, fullscreen-exclusive, other-session, or secure-desktop target. | Disclose before start; never show `Protected`; explain the limitation. |
| Application target — `Unverified` | Check not completed or could not be completed. | No protection claim; offer diagnostics. |
| Agent unavailable | UI cannot reach the local agent. | Offer start/repair/diagnostics; no stale data. |
| Browser policy blocked | Extension or Native Messaging blocked. | Keep other features; no repeated prompts. |

## 8. Accessibility and interaction rules

- All actions are reachable by keyboard.
- Focus order follows information hierarchy.
- Every control has an accessible name and, where needed, help text.
- Status changes use text, not color alone.
- The overlay is focus-trapped only within the blocking surface and always has a visible exit.
- Timers announce state changes without announcing every second.
- Charts have table/text alternatives.
- High contrast, 200% scaling, and reduced motion are supported.
- Errors identify the problem, affected area, and next action.
- No essential interaction depends on hover, sound, or precise pointer movement.
- Windows accessibility tools are not blocked or classified as distractions.

## 9. Required assets and reference locations

All assets are currently missing. The central asset manifest does not yet exist because no supplied files exist.

| Asset area | Purpose | Status |
|---|---|---|
| App icon and logo | Window, tray, installer, store, notifications. | BLOCKED — AWAITING USER REFERENCES |
| Tray state icons | Idle, focus, blocked, break, degraded, service missing. | BLOCKED — AWAITING USER REFERENCES |
| Onboarding illustrations | Privacy, mode comparison, browser setup, first session. | BLOCKED — AWAITING USER REFERENCES |
| Empty-state illustrations | No tasks, no sessions, no insights, no achievements. | BLOCKED — AWAITING USER REFERENCES |
| Protection/status icons | Protected, degraded, unavailable, not configured. | BLOCKED — AWAITING USER REFERENCES |
| Achievement badges | Five MVP achievements. | BLOCKED — AWAITING USER REFERENCES |
| Level/progress visuals | Level indicator and progression surfaces. | BLOCKED — AWAITING USER REFERENCES |
| Error/recovery illustrations | Agent unavailable, extension blocked, database recovery, upgrade failure. | BLOCKED — AWAITING USER REFERENCES |
| Focus/break/summary visuals | Active session, break, session summary. | BLOCKED — AWAITING USER REFERENCES |
| Installer graphics | Full/Limited mode, permissions, completion. | BLOCKED — AWAITING USER REFERENCES |
| Store listing assets | Screenshots, promotional images, privacy visuals. | BLOCKED — AWAITING USER REFERENCES |
| Notification icon | Windows toast identity. | BLOCKED — AWAITING USER REFERENCES |

When references arrive:

1. Inspect every file.
2. Record dimensions, format, variants, and intended usage.
3. Preserve aspect ratio.
4. Update this specification and the gap log.
5. Obtain visual approval before claiming that visual implementation matches a reference.

## 10. UX acceptance criteria

- A first-time user can choose a mode with an accurate comparison and no repeated elevation prompt in Limited mode.
- A user can create a task, start a session, experience a blocked app, complete the session, and review progress.
- No protection status is shown without a verified health check.
- A browser-policy block leaves the rest of the product usable.
- Recovery explains downtime and never silently credits it.
- Upgrade preserves data and leaves Limited mode intact if it fails.
- All core flows are keyboard and screen-reader operable.
- Every screen is either visually approved against a supplied reference or explicitly marked visual-blocked before visual acceptance. Functional acceptance does not require visual approval.
