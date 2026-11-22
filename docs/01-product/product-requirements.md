# Product Requirements Specification

**Product:** Boundless Study  
**Document:** SRS / PRD  
**Status:** Draft for user review  
**Version:** 0.2  
**Last updated:** 2026-11-12  
**Phase:** 2 — Detailed Requirements Specification  
**Related documents:** `../README.md`, `development-roadmap.md`, `../02-requirements/functional-requirements.md`, `../02-requirements/domain-business-rules.md`, `../03-ux/ux-specification.md`

## 1. Executive summary

Boundless Study is a local-first Windows study application that combines a focus timer, task organizer, distraction control, honest study tracking, and lightweight progression. It is designed for students who want structure and momentum without a mandatory account, cloud service, or surveillance model.

The MVP proves one complete loop:

> **Plan → Focus → Block → Measure → Reward → Review**

The first release is deliberately smaller than the full product vision. It must be coherent, reliable, and honest about what it can enforce. Advanced planning, the virtual companion, challenges, a token shop, and themes are deferred, but the architecture must allow them to be added without replacing the core data model or process boundaries.

## 2. Problem

Students face four connected problems:

1. **Starting:** It is unclear what to study next, so study time is delayed or spent on low-value work.
2. **Staying:** Notifications, games, social sites, and other applications interrupt focus.
3. **Knowing:** Students cannot reliably see how much time they actually studied, by subject or task.
4. **Continuing:** Progress is invisible, so motivation depends on willpower alone.

Existing tools usually solve one part: timers, task lists, blockers, or habit trackers. Boundless Study should connect them into one loop without turning studying into surveillance or a competition the student cannot win.

## 3. Vision and product principles

### Vision

A student opens one calm, coherent application, decides what matters today, starts a protected focus session, sees an honest record of the work, and earns visible progress that encourages the next session.

### Principles

1. **Local-first by default.** Core features work without an account or network connection.
2. **Honest protection.** Never display “protected” when enforcement is unavailable or degraded.
3. **One product, not a bundle.** Tasks, sessions, blocking, history, and rewards use the same domain language and data.
4. **Low friction before high control.** The default experience should help, not trap.
5. **Transparent system changes.** The user can see what is installed, what runs, what is monitored, and how to remove it.
6. **Ethical motivation.** No pay-to-win, loot boxes, shame loops, loss aversion, or rewards for merely opening the application.
7. **Accessible by design.** Keyboard operation, screen-reader support, high contrast, scaling, and reduced motion are requirements, not later polish.
8. **Owner-controlled data.** Export, backup, restore, and deletion are first-class features.
9. **No impossible promises.** The product reduces friction for a cooperating user. It is not a parental-control, exam-proctoring, or anti-cheat system.
10. **Modular monolith.** Components execute separately where Windows requires it, but share one domain model.

## 4. Target users

### Primary persona — Maya, university student on a personal laptop

- Windows 11 laptop, local administrator, Chrome or Edge.
- Studies in 25–60 minute blocks with breaks.
- Wants tasks, deadlines, and an honest record of study time.
- Is motivated by levels and achievements but does not want a childish or punitive experience.
- May disable the extension or close the app when frustrated; the product should make the honest choice easy, not pretend it cannot happen.

### Secondary persona — Sam, school-managed laptop without administrator rights

- Cannot install a Windows service or may be blocked from installing browser extensions by policy.
- Needs timers, tasks, tracking, XP, and progress review.
- Must see clearly that application enforcement and browser restrictions are weaker or unavailable.
- Must never be repeatedly prompted for administrator credentials.

### Privacy-sensitive persona — Ari, privacy-conscious student

- Wants no account and no browsing history uploaded.
- Wants to know exactly which data is stored locally.
- Wants export and deletion.
- May choose Limited mode deliberately.

### Jobs to be done

- “Help me decide what to study now.”
- “Protect this block of time from the things I chose to block.”
- “Show me what I actually completed, without judging me.”
- “Let me recover if the session or computer is interrupted.”
- “Give me a reason to come back tomorrow.”
- “Let me keep my data and move between installations without losing progress.”

## 5. Product pillars

| Pillar | MVP promise |
|---|---|
| Plan | Manual subjects and tasks with due dates, estimates, priority, and a today view. |
| Focus | Configurable focus sessions with Flexible, Balanced, and Strict profiles and breaks. |
| Block | Best-effort application and Chrome/Edge website restrictions with visible connection status. |
| Measure | Honest active-study time, session outcomes, and protection gaps. |
| Reward | XP, levels, streaks, and a small achievement set tied to verified events. |
| Review | Daily/weekly totals, subject split, and session history. |
| Own | Local SQLite data, backup, export, restore, deletion, and clean uninstall. |
| Install | Full mode with service supervision, or Limited mode without administrator rights. |

## 6. MVP scope

### 6.1 Included

| Area | MVP requirement |
|---|---|
| Onboarding | Permission detection, mode explanation and selection, browser-extension guidance, limitation disclosure, first task, first session tutorial. |
| Modes | Full mode with Windows service; Limited mode without a service; upgrade path from Limited to Full. |
| Dashboard | Today’s verified study time, streak, next task, protection status, quick start, recent sessions. |
| Study Manager | Subjects, tasks, due dates, estimates, priority, status, today list, task-linked sessions. No automatic scheduling. |
| Focus Engine | Flexible, Balanced, and Strict profiles; focus and break timers; session checkpoints; recovery; early-exit friction; system-interruption handling. |
| Application Guard | User-selected blocked applications; foreground detection; blocking overlay; violations; no forced process termination. Full mode adds service supervision. |
| Browser Guardian | Chrome and Edge MV3 extension; blocked and explicitly allowed domains; connection heartbeat; policy application; visible degraded state. |
| Tracking | Session elapsed time, active-study time, idle time, break time, protection gaps, violations, and end reason. |
| Progression | XP, levels, streak, five MVP achievements, level-up and achievement feedback. No token balance, token earning, shop, or themes. |
| Insights | Today, this week, and last 30 days; subject split; session history; clear metric definitions. |
| Data | SQLite, migrations, automatic backups, manual backup/export, verified restore, integrity checks, diagnostics, clean uninstall. |
| Notifications | Minimal session, break, protection-loss, achievement, and backup-failure notifications. No advanced notification center. |
| System integration | Per-user agent, Full-mode service, native-messaging bridge, named pipes, crash recovery, safe startup, and diagnostics. |
| Accessibility | Keyboard operation, Narrator support, high contrast, 200% scaling, reduced motion, and accessible status messages. |

### 6.2 Explicitly deferred

- Smart Study Planner and automatic scheduling.
- Virtual Study Companion.
- Daily and weekly challenges.
- Token economy, reward shop, themes, and visual customization.
- Scheduled focus profiles.
- Advanced Notification Center and notification inbox.
- Multiple Chromium browsers beyond Chrome and Edge.
- Cloud sync, accounts, mobile, social features, and leaderboards.
- Kernel drivers, process termination as a blocking strategy, and anti-tamper guarantees.
- Parental controls, exam proctoring, or competitive anti-cheat.

### 6.3 Why this boundary

The MVP must validate the core loop and the two installation modes before spending effort on art-heavy or algorithm-heavy features. Deferred modules must remain additive. The data model reserves a currency ledger and achievement structures so XP and future tokens can coexist without recreating the database.

### 6.4 Functionality-first implementation and dedicated UI phase

Implementation prioritizes functional correctness. Early implementation builds working business logic and application services, database operations and persistence, the focus engine and session state, application and browser enforcement; XP, achievements, and progression; and IPC and Windows integration. Features are tested as they become available. A basic functional UI may be used to operate and test the application; it is a temporary interface, not final visual design.

Visual refinement is deferred to a dedicated UI phase after sufficient functional implementation and integration, and before final visual acceptance and release stabilization. At the start of that phase, the user must be explicitly notified: **"This is the UI face."** The user intends to switch to a preferred AI model for dedicated UI development.

The dedicated UI phase must produce a comprehensive specification covering design identity and UI type; navigation and information architecture; every screen, window, dialog, and overlay; layouts and component placement; buttons, cards, tables, forms, menus, and reusable components; interaction behavior and transitions; typography, color, spacing, radii, borders, and elevation; icons, illustrations, and supplied assets; responsive resizing, DPI scaling, and scrolling; loading, empty, success, error, and degraded states; accessibility and keyboard navigation; and consistency and reusable design tokens.

The intended direction is minimal, modern, professional, realistic like a polished desktop productivity application, clear and uncluttered, with features distributed across appropriate screens and no overloaded dashboard. This is a design direction, not a complete visual design system. Supplied visual references remain authoritative, and no visual identity is invented. If references are unavailable when the dedicated UI phase begins, work stops for the user's direction.

Q-001 blocks visual work only. It does not block functional requirements, technical validation, business logic or database implementation, functional navigation, temporary testing interfaces, or feature-level automated testing.

## 7. Installation modes

### 7.1 Mode selection

Setup MUST detect:

- Windows version and architecture.
- Whether the current process is elevated.
- Whether the current account can request elevation.
- Whether a Windows service can be installed.
- Whether Chrome or Edge is installed.
- Whether browser extension installation appears permitted.
- Whether Native Messaging registration is available.
- Whether a previous Boundless Study installation or data store exists.

The user MUST see both modes and their consequences before choosing. Setup MUST NOT silently choose Full mode, request elevation repeatedly in Limited mode, or imply equal enforcement when capabilities differ.

### 7.2 Capability matrix

| Capability | Full mode | Limited mode |
|---|---|---|
| Install without administrator rights | No | Yes |
| Independent Windows service | Yes | No |
| Per-user agent and local data | Yes | Yes |
| Focus sessions, breaks, tasks, subjects | Yes | Yes |
| Study tracking, XP, levels, achievements, insights | Yes | Yes |
| Browser extension and Native Messaging | Yes, where browser policy permits | Yes, where browser policy permits |
| Website restrictions | Yes, where extension is connected and rules apply | Yes, where extension is connected and rules apply |
| Application blocking overlay | Yes, service-supervised; agent-owned foreground detection and overlay | Yes, best effort, unsupervised; same agent-owned foreground detection and overlay mechanisms |
| Elevated, protected, or fullscreen-exclusive application coverage | Not guaranteed; reported `Unsupported` or `Unverified` | Not guaranteed; reported `Unsupported` or `Unverified` |
| Service supervision and recovery of the agent | Yes | No |
| Automatic restart after agent failure | Best effort, service-supervised | User/app restart only |
| Windows service health and install diagnostics | Yes | Not available |
| Explicit degraded-state reporting | Yes | Yes |
| Upgrade to Full mode | Not applicable | Yes, documented and data-preserving |
| Repeated elevation prompts | One installation/upgrade request | None by default |

The same capability matrix is reproduced in the user guide at `../06-user/user-guide.md`. The SRS is authoritative; the user guide must be updated whenever this table changes.

### 7.3 Full mode requirements

- Administrator privileges are required only for installation, upgrade, repair, and uninstallation of privileged components.
- The Windows service runs independently and starts at boot.
- The service supervises the per-user agent, records health, and attempts recovery when the agent stops unexpectedly.
- The service MUST NOT write per-user study data, display interactive UI, terminate arbitrary processes, or install hidden persistence.
- The service and agent MUST report health and failure states. A service that is stopped, missing, or unhealthy MUST produce a visible degraded state when the user next interacts with the product.
- Uninstallation MUST remove the service, its startup registration, named pipes, and privileged files, with a clear data-retention choice.

### 7.4 Limited mode requirements

- Installation, update, and operation MUST NOT require administrator rights or a Windows service.
- Limited mode MUST support focus sessions, tasks, subjects, tracking, XP, levels, achievements, analytics, and local data management.
- Limited mode MUST implement best-effort application blocking through the per-user agent when the supported Windows mechanisms are available. The requirement is to implement the capability and report it honestly, not to guarantee that every application can be blocked.
- Supported without administrator rights: user-session foreground-event detection with a bounded polling fallback, user-session process identity, normalized executable matching, the 3-second grace period, and an agent-owned topmost overlay in the interactive session.
- Not supported by a standard-integrity per-user agent: reliable detection or overlay of elevated or protected processes, fullscreen-exclusive surfaces that suppress or obscure the overlay, applications in other Windows sessions or on the secure desktop, and kernel-level, cross-session, or tamper-proof enforcement. Neither mode uses a kernel driver or process injection.
- If enforcement is unavailable for a target, the session pauses, an enforcement failure is recorded, a persistent notice names the affected target and recovery action, and no blocked time is credited as study. Targets are reported as `Supported`, `Unsupported`, or `Unverified`; `Unsupported` and `Unverified` are never shown as `Protected`.
- Limited mode MUST describe application blocking as best effort and unsupervised. It MUST NOT promise system-wide or tamper-proof enforcement because the agent runs at the signed-in user's integrity level, in one interactive session, and can be stopped by that user. Full mode adds service supervision and recovery, not kernel enforcement or anti-tamper protection.
- Browser integration MUST work when the browser and school policy permit it.
- If extension installation or Native Messaging is blocked by policy, the product MUST explain the limitation and continue with the remaining features.
- Limited mode MUST NOT repeatedly request elevation. A single optional “Learn about Full mode” path may be shown.

### 7.5 Upgrade from Limited to Full

- The upgrade MUST be initiated by the user and explain the additional service and permissions before elevation.
- The upgrade MUST preserve the same local Windows account, data path, profile, tasks, settings, study history, XP, any future token data, and achievements.
- The upgrade MUST NOT reset onboarding, create a second profile, or move the database to a different schema without a tested migration.
- If the upgrade fails or elevation is denied, the installation MUST remain usable in Limited mode with no data loss.
- After a successful upgrade, the service starts, the agent is supervised, and the product reports Full mode only after health checks pass.

### 7.6 Shared functionality and architecture

The shared application core MUST NOT depend on the privileged service. The service is a supervisor and enforcement-support component, not the owner of tasks, sessions, XP, or analytics. Both modes use:

- The same local profile and SQLite schema.
- The same domain and application logic.
- The same per-user agent and data ownership model.
- The same UI.
- The same backup and export format.

Mode differences are capability and permissions differences, not separate products.

## 8. Core user journeys

### J-01 — First run in Limited mode

1. User launches the app without administrator rights.
2. App explains local-first operation and data location.
3. Setup detects no service capability and explains both modes.
4. User selects Limited mode.
5. Setup checks Chrome/Edge and extension availability.
6. If extension installation is permitted, user is guided through installation and connection.
7. If not, app records browser protection as unavailable.
8. User creates a subject and first task.
9. User starts a first focus session with a clear statement of what is and is not protected.

### J-02 — First run in Full mode

1. User starts the installer and requests Full mode.
2. Installer explains the service, permissions, recovery, and uninstall procedure.
3. User approves elevation once.
4. Service, agent, native host, and application are installed.
5. Health checks confirm service, agent, and extension status.
6. App shows Full mode only after checks pass.
7. User completes onboarding and first session.

### J-03 — Plan and start focus

1. User opens Today and selects or creates a task.
2. User chooses Flexible, Balanced, or Strict.
3. User chooses a duration and reviews blocklists and exceptions.
4. App shows a pre-session summary: duration, blocked apps, blocked sites, browser status, and enforcement limits.
5. User starts the session.
6. Agent applies restrictions and starts tracking.

### J-04 — Application violation

1. A blocked application comes to the foreground.
2. Agent waits for the grace period.
3. If the application remains in the foreground, the agent shows a blocking overlay.
4. Overlay names the application, shows remaining session time, and offers “Return to study” or “End session.”
5. Active-study and session timers pause while the overlay is active.
6. User returns to study; the overlay closes and tracking resumes.
7. The violation is recorded without storing window titles or document content.

### J-05 — Browser protection lost

1. Extension heartbeat stops.
2. Agent marks browser protection as degraded.
3. App shows a persistent notice: website restrictions are not currently verified.
4. User can reconnect, continue with the limitation, or end the session.
5. Session summary records the protection gap.
6. App never shows “Protected” during the gap.

### J-06 — Complete a session

1. Focus timer completes.
2. Blocking is lifted.
3. Session summary shows planned time, elapsed time, active-study time, idle time, break time, violations, protection status, and XP earned.
4. XP is awarded once.
5. If a level or achievement is unlocked, it is shown with a functional description.
6. History and insights update.

### J-07 — Recover after interruption

1. Agent or computer stops unexpectedly during a session.
2. Last checkpoint is preserved.
3. On restart, app reports the interruption honestly.
4. User may resume the session if it has not expired; blocking is reapplied.
5. Downtime is not credited as study time.
6. If the session cannot resume, it is closed as interrupted with the checkpointed active time and partial reward eligibility.

### J-08 — Upgrade to Full mode

1. Limited-mode user opens Settings → Installation Mode.
2. App explains the service, stronger supervision, permissions, and data preservation.
3. User chooses Upgrade.
4. Installer requests elevation once and installs privileged components.
5. Agent restarts and connects to the service.
6. App verifies health and reports Full mode.
7. Tasks, settings, study history, XP, achievements, and any future token data if present remain unchanged.
8. If elevation is denied or installation fails, Limited mode remains active with no data change.

### J-09 — Review progress

1. User opens Insights.
2. App shows today, this week, and last 30 days.
3. Metrics are labeled with definitions: elapsed session time, active-study time, break time, idle time, and protection gaps.
4. User can view subject split and session history.
5. No productivity score or social comparison is shown.

### J-10 — Back up, restore, uninstall

1. User opens Data & Backup.
2. User creates a manual backup with a checksum and manifest.
3. Restore validates the backup, creates a safety backup, and verifies integrity.
4. Uninstall offers keep-data or delete-data choices and explains extension removal.
5. Full-mode uninstall removes the service and privileged components.

## 9. Module map

| Module | MVP status | Primary requirements |
|---|---|---|
| Desktop Experience and Dashboard | Included | Dashboard, navigation shell, status surfaces. |
| Focus Engine | Included | Sessions, profiles, breaks, tracking, recovery. |
| Browser Guardian | Included | Chrome/Edge extension, site rules, connection status. |
| Study Manager | Included | Subjects, tasks, today list, history. |
| Gamification and Progression | Included, reduced | XP, levels, streak, achievements. No shop. |
| Analytics and Insights | Included, basic | Daily/weekly totals, subject split, history. |
| Smart Study Planner | Deferred | Manual planning only in MVP. |
| Study Companion | Deferred | No companion art or progression. |
| Daily and Weekly Challenges | Deferred | No challenge engine. |
| Notification Center | Deferred | Minimal system notifications only. |
| Data Management and Backup | Included | Local DB, migrations, backup, restore, export, delete. |
| Windows System Integration | Included | Agent, service, bridge, pipes, install modes, recovery. |

## 10. MVP success criteria

The MVP is acceptable only when all of the following are demonstrated with evidence:

1. A new user can complete onboarding and start a first session in both Full and Limited modes without developer assistance.
2. Full mode installs, reports health, supervises the agent, and uninstalls cleanly.
3. Limited mode installs and runs without elevation and does not repeatedly prompt for administrator rights.
4. Upgrading from Limited to Full preserves tasks, settings, study history, XP, achievements, and any future token data if present.
5. The product never displays “Protected” when the extension, bridge, agent, or service required for that protection is unavailable.
6. A blocked application produces a visible overlay within the declared detection target, without forced termination.
7. A blocked domain is blocked in Chrome or Edge when the extension is connected, and an explicit allow rule wins.
8. A session survives an agent crash or application restart with checkpointed data and no duplicate rewards.
9. XP is awarded exactly once per qualifying event under `ECONOMY-1`, including after restart and repeated completion attempts.
10. Backup, restore, integrity check, migration, and corruption-recovery paths pass destructive tests.
11. Keyboard-only operation, Narrator use, high contrast, 200% scaling, and reduced motion pass the accessibility checklist.
12. No network connection is required for core operation; no telemetry is sent by default.
13. Every visual screen is either approved against a supplied reference or explicitly accepted by the user as visual-blocked.
14. All release-critical requirements have test evidence. Untested claims are not marked complete.

## 11. Product risks and mitigations

| ID | Risk | Impact | Mitigation |
|---|---|---|---|
| R-01 | Scope creep turns the MVP into an unfinished suite. | High | Enforce the included/deferred lists. New MVP features require a documented decision. |
| R-02 | Users interpret “Strict” as unbreakable. | High | Capability disclosure, degraded states, and no anti-tamper claims. |
| R-03 | Extension store review, fixed ID, or school policy blocks distribution. | High | Treat store publishing as a release dependency; support Limited mode without browser control; explain policy failures. |
| R-04 | School-managed PCs block the service or native host. | High | Full/Limited modes, permission detection, graceful degradation. |
| R-05 | Service/agent restart design is unreliable across Windows sessions. | High | Validate early in a technical spike; best-effort watchdog; visible degradation. |
| R-06 | SQLite corruption or failed migration loses data. | High | Backup before migration, integrity checks, restore tests, agent-only database access. |
| R-07 | Visual references never arrive. | Medium | Functional requirements, technical validation, and functionality-first implementation proceed; final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing remain blocked. The dedicated UI phase stops for user direction if references are still absent. |
| R-08 | Gamification rewards duplicate or encourage unhealthy behavior. | Medium | Append-only ledger, idempotency, caps, no app-launch rewards, ethics review. |
| R-09 | Accessibility is deferred and becomes expensive. | Medium | Acceptance tests from the first UI milestone. |
| R-10 | Unsigned binaries trigger SmartScreen or antivirus flags. | Medium | Code signing, no injection, transparent service behavior, clean uninstall. |
| R-11 | Limited mode is mistaken for a lesser product. | Medium | Shared core, same data, clear capability matrix, one-time upgrade path. |
| R-12 | Token data is lost during a future mode or schema change. | Medium | Forward-compatible currency ledger and data-preserving upgrade tests. |

## 12. Assumptions

- A-001: Windows 11 supported builds on x64 are the MVP target. ARM64 and Windows 10 are not MVP commitments.
- A-002: Chrome stable and Edge stable are the MVP browsers.
- A-003: English is the only MVP UI language; strings remain localization-ready.
- A-004: One local Windows user profile owns one Boundless Study profile.
- A-005: The user can install the browser extension through the browser’s supported mechanism. The product cannot silently install it.
- A-006: The user may be a local administrator, a standard user, or a user on a managed device.
- A-007: The application does not require a network connection for core functionality.
- A-008: The database lives under `%LocalAppData%\BoundlessStudy\` and is owned by the per-user agent.
- A-009: Backups and exports are local files; the product does not upload them.
- A-010: `ECONOMY-1` is the frozen MVP economy baseline defined in `domain-business-rules.md`. Its XP rates, multipliers, minimum active time, daily cap, task XP, achievement XP, level curve, and streak eligibility are fixed for acceptance testing. Future tuning requires a new economy version; historical ledger entries are never silently recalculated.

## 13. Non-goals

- Preventing a determined administrator from bypassing enforcement.
- Monitoring browsing history, keystrokes, screen content, or document content.
- Parental controls, employee monitoring, exam proctoring, or disciplinary reporting.
- Cloud accounts, social comparison, leaderboards, or real-money purchases.
- Replacing a full task manager, calendar, or learning-management system.
- Automatic categorization of educational versus distracting websites.
- Guaranteed blocking of every browser, app, virtual machine, or network path.
- Visual design without supplied references.

## 14. Release gate

The MVP cannot enter implementation without:

1. User approval of this SRS and the architecture proposal.
2. Resolution or explicit acceptance of open questions Q-001 through Q-007. Q-005 and Q-007 are resolved. Q-001 is accepted as a visual-only blocker. Q-006 is resolved with conditions. Q-002 is provisionally recommended as Inno Setup 7.x (with 64-bit install mode per the closure-round finding R-P3-13) and requires user approval. Q-003 is partially resolved: the supervision loop was verified in user mode in the 2026-11-13 closure round, and the session-0 service remainder has an explicit owner and elevated runbook. Q-004 is partially verified: store requirements and the official extension-ID-stability workflow are documented, with store accounts, final store ID, and code signing still external. Q-001 does not block functional implementation or technical validation, but it blocks final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing until references are supplied.
3. A signed-off functional requirements baseline.
4. A signed-off test strategy and acceptance traceability approach.
5. Explicit authorization to begin development.
