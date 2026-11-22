# Boundless Study — User Guide (Draft)

**Product:** Boundless Study  
**Document:** User guide draft  
**Status:** Draft for user review. Not release documentation. No product is implemented yet.  
**Version:** 0.1  
**Last updated:** 2026-11-12  
**Applies to:** Planned MVP  
**Related:** `../01-product/product-requirements.md`, `../02-requirements/functional-requirements.md`, `../03-ux/ux-specification.md`

> This guide describes the planned MVP behavior. It is a documentation deliverable, not evidence that the application, installer, service, or browser extension exists or has passed testing. Visual descriptions are intentionally absent because no UI references have been supplied.

## 1. What Boundless Study is

Boundless Study is a local-first Windows study application. It combines:

- A task list with subjects, due dates, estimates, and a manual today list.
- Focus sessions with configurable profiles and breaks.
- Best-effort blocking of applications and websites you choose.
- An honest record of session time, active-study time, idle time, and protection gaps.
- XP, levels, streaks, and achievements.
- Daily, weekly, and 30-day progress review.

It is designed for a student working on their own computer. It is not a parental-control product, an exam-proctoring tool, an anti-cheat system, or a guarantee that distractions cannot be accessed.

### 1.1 What “local-first” means

- You do not need an account.
- Your tasks, sessions, settings, and progress are stored on your computer.
- Core features work without a network connection.
- The MVP does not upload study data or send telemetry.
- Backups and exports are files you create and control.

### 1.2 What Boundless Study does not promise

- It does not make distraction impossible.
- It does not provide system-wide or tamper-proof application blocking.
- It does not stop a determined administrator or device owner from bypassing it.
- It does not monitor browsing history, page contents, keystrokes, screenshots, or document contents.
- It does not terminate applications you have blocked. It shows a blocking surface and asks you to return to study.
- It cannot block every browser, application, virtual machine, or separate device.
- It cannot cover browser surfaces outside the extension’s scope, such as some internal pages or unapproved browser profiles.

## 2. Choose an installation mode

Boundless Study has two installation modes. Both use the same local study profile, the same data location, and the same study features. The difference is installation permission and the strength of supervision.

Setup checks your environment before asking you to choose. It reports:

- Windows version and architecture.
- Whether the installer is elevated.
- Whether your account can request administrator rights.
- Whether a Windows service can be installed.
- Whether Chrome or Edge is installed.
- Whether extension installation appears permitted by browser policy.
- Whether Native Messaging registration is available.
- Whether existing Boundless Study data or an existing installation was found.

Setup explains both modes before selection. It does not silently choose Full mode.

### 2.1 Capability comparison

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

### 2.2 Full mode

Choose Full mode when:

- You can provide administrator rights once during installation.
- You want the application to install an independent Windows service.
- You want the service to supervise the per-user agent and attempt recovery if it stops unexpectedly.
- You want the stronger service-backed supervision that Full mode provides.
- Your device policy allows a Windows service and browser Native Messaging.

Full mode:

- Requires administrator rights for installation, upgrade, repair, and uninstallation of privileged components.
- Installs a Windows service that starts at boot.
- Supervises the per-user agent and records health.
- Can attempt a documented restart when the agent stops unexpectedly.
- Supports the Chrome/Edge extension and Native Messaging where policy permits.
- Keeps your study data in your user profile, not in the service.
- Shows a degraded state if the service is stopped, missing, or unhealthy.

Full mode still does not guarantee that protection cannot be bypassed.

### 2.3 Limited mode

Choose Limited mode when:

- You cannot or do not want to provide administrator rights.
- You are using a school-managed computer that blocks services or elevation.
- You want the core study features without a privileged installation.
- Your browser policy may block the extension; the app will still work without website blocking.

Limited mode:

- Installs and runs without administrator rights.
- Does not install or require a Windows service.
- Supports focus sessions, tasks, subjects, tracking, XP, progression, and analytics.
- Supports the browser extension and website restrictions when browser and school policy permit.
- Implements application blocking through the per-user agent on a best-effort basis, using user-session foreground detection and an agent-owned overlay.
- Does not require administrator rights for that best-effort application blocking.
- Cannot reliably detect or overlay elevated or protected processes, fullscreen-exclusive surfaces that suppress overlays, applications in other Windows sessions, or the secure desktop.
- Reports each blocked application target as `Supported`, `Unsupported`, or `Unverified` before the session. Unsupported or unverified targets are never shown as protected.
- Describes application blocking as best effort and unsupervised.
- Never displays protection as active when it is not verified.
- Does not repeatedly ask for administrator rights. An optional “Learn about Full mode” path is available, but it does not nag.
- Remains fully usable if browser extension installation or Native Messaging is blocked.

### 2.4 School-managed devices

A school may block:

- Installation of a Windows service.
- Administrator elevation.
- Browser extension installation.
- Native Messaging.
- Some browser settings, including incognito behavior.

Boundless Study must report these restrictions clearly and continue with the features that remain available. A blocked browser capability affects website protection, not tasks, timers, study history, XP, or insights.

## 3. Install Boundless Study

### 3.1 Before installation

1. Close other study timers or blockers that may conflict.
2. Decide whether you want Full or Limited mode.
3. If you use Chrome or Edge, confirm that you can install an extension.
4. Confirm you have enough local disk space for the application, database, logs, and backups.

### 3.2 Full mode installation

1. Start the Boundless Study installer.
2. Review the mode comparison.
3. Select Full mode.
4. Read the permission explanation: Windows service, administrator elevation, agent supervision, recovery, native host registration, and uninstall.
5. Approve the elevation request.
6. Wait for installation and health checks.
7. Boundless Study reports Full mode only after the service, agent, database, and required components pass health checks.
8. Continue with browser setup and onboarding.

If you decline elevation or installation fails, you can continue in Limited mode. Existing data must remain unchanged.

### 3.3 Limited mode installation

1. Start the Boundless Study installer as your normal user.
2. Review the mode comparison.
3. Select Limited mode.
4. Confirm that no Windows service will be installed.
5. Complete the per-user installation. No administrator prompt should appear.
6. Continue with browser setup and onboarding.

If browser policy blocks the extension, onboarding explains the limitation and lets you finish with the remaining features.

### 3.4 Upgrade from Limited to Full mode

1. Open **Settings → Installation Mode**.
2. Review the current mode, capabilities, and health.
3. Select **Upgrade to Full mode**.
4. Read what will be installed: the Windows service, privileged components, native host registration, and agent supervision.
5. Confirm the single elevation request.
6. Wait for installation, service start, agent restart, and health checks.
7. Full mode is reported only after health checks pass.

The upgrade preserves:

- Your Windows user profile and Boundless Study profile.
- Tasks, subjects, notes, and settings.
- Study history, focus sessions, breaks, and events.
- XP, levels, streaks, and achievements.
- Any token entries created by a future version that supports tokens. The MVP does not display a token balance or expose token earning or spending.

If elevation is denied or the upgrade fails, Limited mode remains active and your data is not changed.

## 4. First run

Onboarding explains:

1. Where your data is stored.
2. That no account is required.
3. That core features work offline.
4. Which installation mode is active and what it can and cannot enforce.
5. How to install or connect the browser extension.
6. What application blocking does and does not do.
7. How to end a session, recover after an interruption, and uninstall.

You can create a first subject and task, or skip and add them later. Before your first focus session, a tutorial explains blocking, limitations, early exit, recovery, and uninstallation.

## 5. Plan your study

### 5.1 Subjects

A subject groups related tasks. You can:

- Create a subject with a name and optional description.
- Rename or describe it later.
- Archive it when it is no longer active.

Archiving a subject does not delete its tasks or history.

### 5.2 Tasks

A task can include:

- Title.
- Subject.
- Due date.
- Estimated minutes.
- Priority: Low, Normal, or High.
- Status: Todo, In Progress, Done, or Archived.
- Notes.

The MVP does not automatically schedule tasks. The today list is manual: you choose which tasks to work on today.

### 5.3 Completing tasks

Marking a task Done awards task XP once. Reopening and completing it again does not award more XP. Deleting a task is a soft delete so past session history remains readable.

## 6. Start a focus session

1. Choose a task or start without one.
2. Choose a focus profile: Flexible, Balanced, or Strict.
3. Choose a duration.
4. Review the pre-session summary.
5. Start the session.

The pre-session summary shows:

- Planned duration.
- Profile behavior.
- Number of blocked applications and websites.
- Browser protection status.
- Temporary exception policy.
- What enforcement can and cannot do.

### 6.1 Focus profiles

| Rule | Flexible | Balanced | Strict |
|---|---|---|---|
| Block configured apps and sites | No | Yes | Yes |
| Early-exit friction | None | Confirmation and 10-second cooldown | Typed confirmation and 30-second cooldown |
| Temporary exceptions | Not applicable | Up to 2 per session, 5 minutes each | Up to 1 per session, 2 minutes, after a 30-second delay |
| Browser extension required when website rules exist | No | No, but protection is shown as unavailable | Yes, before the session can start |
| XP per active minute | 2 | 3 | 4 |

A profile blocks only the applications and websites you configured. It does not block everything. Strict does not mean unbreakable.

### 6.2 Session time

- **Session elapsed time** is the focus countdown. It pauses when the machine is locked or suspended and while a blocking overlay is active.
- **Active-study time** is the part of the session that counts as study. It stops after 5 minutes without keyboard or pointer input and resumes when you return.
- **Idle time** is time after that threshold with no input.
- **Break time** is measured separately and earns no XP.
- **Protection gap** is time when configured website rules were not verified as applied.

Reading or watching without input may undercount active-study time. The summary labels the metric so the difference is visible.

### 6.3 Breaks

A break can start only when no focus session is active. Break durations are 1–60 minutes, with a 5-minute default. Blocking is lifted during a break. Break time does not earn XP or change your streak.

## 7. Blocking behavior

### 7.1 Applications

When a blocked application comes to the foreground:

1. The agent waits 3 seconds.
2. If the application remains in the foreground, a blocking overlay appears.
3. The overlay names the application and shows the remaining focus time.
4. Choose **Return to study** or **End session**.
5. Session time and active-study time pause while the overlay is visible.

Boundless Study does not terminate, suspend, or modify the blocked application. System-critical processes, accessibility tools, Boundless Study components, and the service are excluded from blocking.

Application blocking works the same way in Full and Limited modes: the per-user agent watches foreground changes and shows its own overlay. Full mode adds service supervision and recovery if the agent stops; it does not add system-wide or tamper-proof blocking.

Some applications are outside best-effort coverage:

- Elevated or protected processes may not be detected or overlaid by a standard-integrity agent.
- Fullscreen-exclusive applications and some games may hide or suppress the overlay.
- Applications in other Windows sessions or on the secure desktop are not covered.
- No kernel driver, process injection, or system-wide enforcement is used in either mode.

Before a session, each blocked application is reported as `Supported`, `Unsupported`, or `Unverified`. If the overlay cannot be shown, the session pauses and records an enforcement failure instead of counting blocked time as study. The app shows a persistent notice naming the affected target and a recovery action. An unsupported target is never shown as protected.

### 7.2 Websites

Website rules apply to Chrome and Edge through the Boundless Study extension when it is installed and connected.

- Blocked domains match their subdomains.
- An explicit allowed domain takes precedence over a blocked domain.
- URL paths, query strings, regular expressions, and wildcard top-level domains are not supported in the MVP.
- Rules apply only during an active focus session and expire when the session ends.
- Browser internal pages, other browsers, guest profiles, and unapproved browser profiles are outside the extension’s scope.
- Incognito coverage is not guaranteed. The app reports when incognito access is unavailable.

The extension does not collect browsing history, page content, cookies, or form data.

### 7.3 Temporary exceptions

Balanced mode allows up to two temporary exceptions per session, each up to 5 minutes. Strict allows one exception, up to 2 minutes, after a 30-second delay. Flexible does not use blocking.

An exception applies only to the selected application or domain and expires automatically. The block resumes when it expires.

### 7.4 Protection status

| Status | What it means |
|---|---|
| Protected | The specific protection surface is verified active. |
| Degraded | Protection was active but is no longer verified. |
| Unavailable | The required component is absent or blocked by policy. |
| Not configured | No application or website rules exist for this session. |
| Not required | The selected profile does not use that protection surface. |
| Unknown | The check has not completed. |

“Protected” always refers to a specific surface, such as website rules in Chrome. It is never a general promise that distractions are impossible.

Application targets use a separate coverage label:

- `Supported` — the agent can detect the user-session target and show the overlay at its integrity level.
- `Unsupported` — elevated, protected, fullscreen-exclusive, other-session, or secure-desktop target. It is not shown as protected.
- `Unverified` — the check has not completed or could not be completed. It is not shown as protected.

## 8. Complete a session

When the focus timer reaches the planned duration:

1. Blocking is lifted.
2. The session summary shows planned time, elapsed time, active-study time, idle time, break time, violations, protection gaps, profile, and outcome.
3. XP is awarded once if the session qualifies.
4. Level and achievement feedback is queued until the session reaches a terminal state and is shown before the next session starts. It is never shown over a blocking overlay or during a confirmation countdown.
5. Insights and session history update.

A session qualifies for XP and streak credit when it is completed and has at least 10 minutes of active-study time. Ending early may earn partial XP but does not qualify for the streak.

## 9. Progress and rewards

### 9.1 XP

The MVP uses Economy Rule Version 1 (`ECONOMY-1`). These values are fixed for this release. A later tuning change would create a new version and would not rewrite past rewards.

- Session XP uses the profile rate and active-study minutes.
- You need at least 10 active-study minutes to earn session XP.
- Completed sessions use a 1.0 multiplier.
- Ended-early and abandoned sessions use a 0.5 multiplier.
- XP is rounded down to a whole number after the active-minute calculation.
- Session XP is capped at 300 XP per local day. A final session can earn only the remaining balance; unused cap does not carry to another day.
- Completing a task awards 25 XP once.
- Achievements award fixed XP once: 50, 75, 100, 100, and 75 XP.
- Task and achievement XP do not count against the session daily cap.
- Each completed session summary shows the economy rule version used to calculate its XP.

### 9.2 Levels

Levels use 500 XP per level, up to level 50. The formula is `min(50, floor(total XP / 500) + 1)`. Total XP continues to be recorded after level 50.

### 9.3 Streaks

A qualified day has at least one completed focus session with 10 or more active-study minutes. The current streak increments on the next qualified day. A missed day resets the current streak to zero until the next qualified day, which starts a new streak at one. The longest streak is retained.

The MVP has no streak freeze, purchase, or retroactive repair.

### 9.4 Achievements

The MVP includes five achievements:

| Achievement | Criteria |
|---|---|
| First Focus | Complete the first qualified focus session. |
| One Hour | Accumulate 60 verified active-study minutes. |
| Three-Day Rhythm | Reach a 3-day streak. |
| Deep Work | Complete 5 Strict focus sessions with at least 10 active minutes. |
| Task Finisher | Complete 10 tasks. Progress uses lifetime completed-task history; archiving or reopening a task does not remove earned progress or duplicate rewards. |

Achievement names and criteria are functional product copy. Badge visuals remain blocked until references are supplied.

### 9.5 Tokens

The MVP does not award, show, or spend tokens. The data model reserves a token currency so that a future release can add tokens without losing progression during an installation-mode upgrade or migration.

## 10. Your data

### 10.1 Where data is stored

By default:

- Database: `%LocalAppData%\BoundlessStudy\boundless.db`
- Logs: `%LocalAppData%\BoundlessStudy\Logs\`
- Backups: `%LocalAppData%\BoundlessStudy\Backups\`

Both installation modes use the same path and schema. The per-user agent is the sole database owner and the only component that opens it, for reads and writes.

### 10.2 Backup and restore

You can create a manual backup with a manifest, schema version, application version, and checksum. Restore validates the backup, creates a safety backup, verifies integrity, and restarts the agent. A corrupted or incompatible backup is rejected without overwriting your current data.

Automatic backups keep the seven most recent backups plus the latest pre-migration backup.

### 10.3 Export

You can export subjects, tasks, sessions, rewards, and achievements as a local ZIP archive of UTF-8 JSON files. The archive contains `manifest.json`, `subjects.json`, `tasks.json`, `sessions.json`, `rewards.json`, and `achievements.json`. The manifest records the schema, app, and economy versions, the export time, record counts, and a SHA-256 checksum for each file. The files can be opened without Boundless Study.

### 10.4 Delete all data

Delete all data requires explicit confirmation. It stops active sessions and removes the database, backups, logs, and local settings. This action is irreversible.

### 10.5 Data access limitation

The database is local and is not encrypted at rest in the MVP. Your Windows account and local administrators can read the files. This limitation is disclosed rather than hidden.

## 11. Recovery

### 11.1 Application or computer interruption

Boundless Study checkpoints an active session at least every 10 seconds.

After an interruption:

1. The last checkpoint is loaded.
2. Downtime is not credited.
3. A recovery notice explains what happened and what time was restored.
4. You can resume the session if it has not expired, or abandon it.
5. Resuming reapplies the configured restrictions.

If the checkpoint is older than 24 hours or cannot be reconciled, the session is closed as abandoned.

### 11.2 Full mode service problems

If the Windows service is stopped or cannot restart the agent, the app shows degraded supervision. It does not claim that Full-mode supervision is active. Follow the repair guidance in Diagnostics or restart the service through the documented recovery flow.

### 11.3 Limited mode agent problems

Limited mode has no service supervision. If the agent stops, reopen the app or use the repair/start path. An interrupted session recovers from its checkpoint when the agent starts again.

### 11.4 Browser protection lost

If the extension heartbeat stops, website protection becomes degraded within 30 seconds. The session continues, a protection gap is recorded, and the app shows a persistent notice. You can reconnect the extension, run the browser connection test, or end the session.

## 12. Uninstall

### 12.1 Full mode

Uninstall removes:

- The Windows service.
- The per-user agent launch task.
- Native Messaging registration.
- Named pipes.
- Privileged application files.

You choose whether to keep or delete your study data. The browser extension must be removed from the browser separately; the app provides instructions.

### 12.2 Limited mode

Uninstall removes the per-user startup registration, application files, and Native Messaging registration. You choose whether to keep or delete your study data. The browser extension must be removed separately.

### 12.3 Keeping data

If you keep data, a later installation detects the existing database and offers to use it. It does not silently overwrite or reset it.

## 13. Troubleshooting

| Problem | What to check |
|---|---|
| Setup says Full mode is unavailable | Your account may not be able to elevate, or policy may block service installation. Continue in Limited mode or ask your administrator. |
| Limited mode asks for administrator rights | It should not, except when you explicitly choose the Full-mode upgrade. Cancel and report the prompt as a defect. |
| Browser extension is blocked by policy | Website protection is unavailable. Tasks, timers, tracking, XP, and insights still work. |
| Browser extension is installed but not connected | Run the browser connection test. Check the native host registration and whether the browser blocks Native Messaging. |
| Website protection is degraded | The extension or bridge stopped responding. Reconnect it or continue with a recorded protection gap. |
| Blocked application overlay does not appear | The application may be fullscreen-exclusive or protected. The session should pause and record an enforcement failure. |
| Agent is unavailable | Start or repair the agent from the app. In Full mode, check service health. |
| Database is locked | Wait for the current operation to finish. The agent retries with a bounded wait. |
| Backup failed | Check free disk space, the backup path, and file permissions. The app keeps the backup status unhealthy until resolved. |
| Upgrade failed | Limited mode remains active and data is unchanged. Review Diagnostics, then retry or continue in Limited mode. |

## 14. Privacy summary

Boundless Study stores locally:

- Your local profile and optional display name.
- Subjects, tasks, and notes.
- Focus and break sessions, events, and timing metrics.
- Blocked and allowed applications and domains.
- XP, achievements, and future token entries if present.
- Settings, logs, and backups.

It does not collect or transmit:

- Browsing history or URLs.
- Page titles or content.
- Cookies or form data.
- Keystrokes, screenshots, clipboard contents, or document contents.
- Telemetry or analytics.

Diagnostics are exported only when you request them and are checked for personal content before export.

## 15. Known limitations

- Enforcement is best effort.
- A local administrator or device owner can bypass or remove components.
- Application blocking is not system-wide or tamper-proof. Elevated or protected processes, fullscreen-exclusive surfaces, other Windows sessions, and the secure desktop may be outside coverage.
- Fullscreen-exclusive applications may prevent or obscure the blocking overlay.
- Chrome and Edge are the only supported browsers in the MVP.
- Browser internal pages, other browsers, guest profiles, and unapproved browser profiles are not covered.
- Incognito coverage is not guaranteed.
- Website blocking requires the extension, native bridge, and agent to be healthy.
- School policy may block the service, extension, or Native Messaging.
- The MVP has no cloud sync, account recovery, mobile app, or social features.
- The MVP has no token earning, token shop, themes, companion, or challenges.
- The database is not encrypted at rest in the MVP.
- Visual design and assets are not specified until user references are supplied.

## 16. Document status

This guide is a draft. It must be updated with:

- Final screen names and navigation labels after visual references are supplied.
- Installation screenshots or descriptions after the installer is implemented.
- Exact error messages and recovery actions after the error catalog exists.
- Confirmed Windows builds, browser versions, extension ID, and store links after release decisions.
- Tested results. Nothing in this guide is a claim that a feature has passed testing.
