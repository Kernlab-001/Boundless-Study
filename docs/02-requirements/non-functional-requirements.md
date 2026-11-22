# Non-Functional Requirements

**Product:** Boundless Study  
**Status:** Draft for user review  
**Version:** 0.1  
**Last updated:** 2026-11-12  
**Related:** `functional-requirements.md`, `domain-business-rules.md`, `../04-architecture/system-architecture.md`, `../05-quality/test-strategy.md`

## 1. Purpose

These requirements define quality attributes for the MVP. They are release criteria, not claims. Each target must be measured on the reference environment or a documented equivalent before it is marked verified.

Priority follows the same MUST / SHOULD / MAY convention as the functional requirements.

## 2. Reference environment

Unless a requirement states otherwise, performance is measured on:

- Windows 11 supported build, x64.
- 4 physical or 8 logical cores.
- 8 GB RAM.
- SSD with at least 10 GB free.
- Integrated graphics.
- Chrome stable and Edge stable, one browser running.
- 100,000 historical focus sessions and 1,000,000 session events in the test database.
- 200% display scaling on one monitor, plus a mixed-DPI dual-monitor test.

Results on lower-end school devices must be recorded separately if the product is distributed to them.

## 3. Reliability and data integrity

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-REL-001 | MUST | A crash, forced termination, power loss, or reboot must not corrupt the database or duplicate a reward. | Destructive crash and recovery tests at multiple session phases. |
| BS-NFR-REL-002 | MUST | Session checkpoints must limit lost session data to one checkpoint interval, never credit downtime, and never invent completion. | Kill agent at 0%, 50%, and 99% of a session; compare timestamps, elapsed, active, and rewards. |
| BS-NFR-REL-003 | MUST | Reward and source-state changes are atomic. A failure leaves neither a completed source without reward nor a reward without a source. | Fault injection around ledger and session transactions. |
| BS-NFR-REL-004 | MUST | A migration failure leaves the pre-migration database readable and recoverable. | Fail migration at each step; verify backup and restore. |
| BS-NFR-REL-005 | MUST | The agent recovers from a locked or busy database with bounded retries and a visible wait/error state; all readers are agent-mediated. | Concurrent agent-mediated reader and single-writer lock-hold tests, including timeout and cancellation. |
| BS-NFR-REL-006 | MUST | Full mode reports service or agent failure and attempts a documented restart; failure is visible, not silent. | Stop agent, stop service, deny restart, and test logon/logoff. |
| BS-NFR-REL-007 | MUST | Limited mode remains usable when service components are absent and when browser policy blocks the extension. | Standard-user and policy-restricted test machines. |
| BS-NFR-REL-008 | MUST | An eight-hour soak test with repeated sessions, breaks, idle periods, sleep/resume, and browser reconnects completes without unbounded memory growth or data loss. | Automated soak run with memory and error telemetry captured locally. |
| BS-NFR-REL-009 | MUST | Backup and restore are verified with a checksum, schema version, and integrity check before and after restore. | Restore from valid, truncated, corrupted, and incompatible backups. |
| BS-NFR-REL-010 | MUST | No unsupported automatic repair deletes user history. Corruption enters a recovery state and preserves the original file. | Corrupt database and inspect quarantine behavior. |

## 4. Performance and resource use

| ID | Pri | Requirement | Target |
|---|---|---|---|
| BS-NFR-PERF-001 | MUST | UI cold start to interactive dashboard on the reference environment. | ≤ 3 seconds, measured from process start to a usable dashboard. |
| BS-NFR-PERF-002 | MUST | Warm UI start. | ≤ 1.5 seconds. |
| BS-NFR-PERF-003 | MUST | Agent start to ready state. | ≤ 2 seconds, excluding Windows logon contention. |
| BS-NFR-PERF-004 | MUST | Foreground change to blocked-app detection. | ≤ 500 ms after the foreground change; overlay after the 3-second grace period. |
| BS-NFR-PERF-005 | MUST | Session start to browser rules applied and status verified. | ≤ 2 seconds when the extension and bridge are healthy. |
| BS-NFR-PERF-006 | MUST | Extension heartbeat loss to degraded status. | ≤ 30 seconds. |
| BS-NFR-PERF-007 | MUST | Common database operations (task list, dashboard summary, session summary). | p95 ≤ 100 ms on the reference database. |
| BS-NFR-PERF-008 | MUST | Insights for today, this week, and last 30 days. | p95 ≤ 1 second for 100,000 sessions. |
| BS-NFR-PERF-009 | SHOULD | UI must not block for more than 100 ms on the UI thread during normal navigation. | No frozen-window report in performance tests. |
| BS-NFR-PERF-010 | MUST | Agent idle CPU. | ≤ 1% average over 10 minutes with no session. |
| BS-NFR-PERF-011 | MUST | Agent active CPU. | ≤ 5% average during a focus session with one blocked app event per minute. |
| BS-NFR-PERF-012 | SHOULD | Agent working set. | ≤ 100 MB during a normal session. |
| BS-NFR-PERF-013 | SHOULD | UI working set. | ≤ 250 MB after navigating all primary screens. |
| BS-NFR-PERF-014 | MUST | Session checkpoint write interval. | ≤ 10 seconds, with bounded write amplification. |
| BS-NFR-PERF-015 | MUST | Installer and uninstaller progress is visible; no operation should appear frozen for more than 5 seconds without status text. | Manual installation test. |

These are targets for verification. They must not be presented as achieved until measured and recorded.

## 5. Security

### 5.1 Threat model

The product protects against casual bypass and accidental misuse. It does not protect against the local owner, a local administrator, a malicious administrator, physical access, or a compromised operating system.

| Threat | Impact | Control |
|---|---|---|
| Another standard user reads or writes study data | Privacy loss / tampering | User-scoped ACLs on LocalAppData directories and pipes; per-user agents. |
| Malicious local process impersonates the UI or bridge | Unauthorized session changes | Pipe ACLs, process/user validation, role-restricted endpoints, bounded schemas. |
| Malformed or oversized IPC message crashes the agent | Availability | Length-prefixed messages, maximum sizes, schema validation, timeouts, error isolation. |
| Extension from an unexpected origin connects to the bridge | Policy tampering | Native Messaging allowed-origin list and fixed extension ID. |
| Remote code or network access in the extension | Supply-chain/privacy risk | No remote code, MV3 CSP, minimal permissions, no eval, store review. |
| Unsigned installer or update | Malware / SmartScreen distrust | Code signing, version checks, no silent unverified updates. |
| Logs or diagnostics leak personal content | Privacy | No URLs, titles, notes, names, or content; redaction checklist; user-initiated export. |
| Backup file exposes study data | Privacy | User-scoped ACL, explicit local path, no automatic upload. |
| Service overreach | Security / trust | Least-privilege validation, no arbitrary process termination, no user-data writes, documented uninstall. |
| User disables protection | Expected | Truthful status, no anti-tamper claim. |

### 5.2 Security requirements

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-SEC-001 | MUST | All local IPC endpoints are user-scoped and require both user identity and client-role validation. | Attempt connections from another user and an unapproved process. |
| BS-NFR-SEC-002 | MUST | The agent rejects malformed, oversized, unknown-version, and unauthorized messages without crashing or mutating data. | Fuzz and protocol-contract tests. |
| BS-NFR-SEC-003 | MUST | The native host accepts only the allowlisted browser extension origin and fixed extension ID. | Mismatched-origin test. |
| BS-NFR-SEC-004 | MUST | The extension uses the minimum permissions needed for declarative blocking and Native Messaging; it does not request browsing history, cookies, bookmarks, or page content. | Permission review and store submission checklist. |
| BS-NFR-SEC-005 | MUST | The extension does not execute remote code or use unsafe dynamic evaluation. | CSP and source review. |
| BS-NFR-SEC-006 | MUST | The service runs with the least privilege that can perform documented supervision and does not expose a network listener. | Service account review and netstat inspection. |
| BS-NFR-SEC-007 | MUST | The service does not terminate arbitrary processes, inject code, modify other applications, or install undocumented autostart entries. | Runtime and installer inspection. |
| BS-NFR-SEC-008 | MUST | Installer, main binaries, service, and native host are code-signed; release artifacts are hash-verified. | Signature verification on a clean VM. |
| BS-NFR-SEC-009 | MUST | Database, backup, and log files use user-scoped ACLs and do not grant broad machine access. | ACL inspection with a second standard user. |
| BS-NFR-SEC-010 | SHOULD | The application detects unsupported or modified privileged components and reports degraded trust rather than silently trusting them. | Tamper-state diagnostics. |
| BS-NFR-SEC-011 | MUST | No secrets, credentials, API keys, or tokens are stored in source, logs, or client files. | Secret scan and release review. |
| BS-NFR-SEC-012 | MUST | Restore and import validate file type, size, checksum, schema, and path; no path traversal or arbitrary file overwrite is possible. | Malicious archive tests. |
| BS-NFR-SEC-013 | MUST | Any future auto-update mechanism verifies signatures and cannot downgrade silently. | Update test; update is not in MVP unless separately approved. |

### 5.3 Security non-goals

- Protecting against a determined local administrator.
- Anti-cheat, exam proctoring, parental control, or employee monitoring.
- Guaranteed detection of every blocked process or website bypass.
- Encrypting the local database against the signed-in Windows user. Encryption at rest is not an MVP claim; the limitation is disclosed.

## 6. Privacy and data protection

### 6.1 Data inventory

| Data | Purpose | Local only | Retention |
|---|---|---|---|
| Windows account SID / local profile ID | Isolation and ownership | Yes | Until data deletion |
| Display name (optional) | Personalization | Yes | Until changed/deleted |
| Subjects and tasks | Study organization | Yes | Until deleted |
| Focus/break sessions and events | History and recovery | Yes | Until deleted |
| Active-study and idle metrics | Progress and insights | Yes | Until deleted |
| Blocked/allowed domains | Enforcement configuration | Yes | Until deleted |
| Blocked application paths/names | Enforcement configuration | Yes | Until deleted |
| XP, future tokens, achievements | Progression | Yes | Until deleted |
| Settings and preferences | Product behavior | Yes | Until deleted |
| Local logs | Diagnostics and support | Yes | Rotated |
| Backup files | Recovery | Yes | Retention policy |
| Browser status/heartbeats | Protection health | Yes | Ephemeral/log-limited |

The product does not collect browsing history, page titles, URLs, cookies, form data, keystrokes, screenshots, document contents, or clipboard contents.

### 6.2 Privacy requirements

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-PRV-001 | MUST | Core functionality requires no account, cloud connection, or telemetry. | Offline acceptance suite and network capture. |
| BS-NFR-PRV-002 | MUST | The product discloses exactly what is stored, where, and why before onboarding completes. | Privacy content review and onboarding test. |
| BS-NFR-PRV-003 | MUST | The product does not transmit study data, blocked domains, app names, or diagnostics automatically. | Network capture and code review. |
| BS-NFR-PRV-004 | MUST | Diagnostics export is user-initiated and excludes task notes, subject/task names, URLs, titles, and content. | Redaction test with seeded sensitive strings. |
| BS-NFR-PRV-005 | MUST | Logs exclude browsing URLs, window titles, document names, task notes, and content. | Seeded-string log inspection. |
| BS-NFR-PRV-006 | MUST | The user can export and delete all local data. | Data deletion test including backups, logs, and settings. |
| BS-NFR-PRV-007 | MUST | Backups are local and never uploaded automatically. | Network capture. |
| BS-NFR-PRV-008 | MUST | Future optional network features require explicit informed consent and a separate scope decision. | No opt-in network feature in MVP. |
| BS-NFR-PRV-009 | SHOULD | A concise, user-facing privacy summary is reachable from onboarding, About, and the distribution point; it states what is stored locally, that no telemetry is sent, and how the user can export or delete data. The store-compliant privacy policy and submission artifacts are governed by BS-NFR-RLS-002. | Content review against the privacy inventory; link-reachability test from onboarding, About, and distribution point. |
| BS-NFR-PRV-010 | MUST | School-managed policy restrictions are reported without collecting additional device information. | Diagnostics review. |

### 6.3 Privacy limitations

- Local administrators can access the database and backups.
- The Windows account may be managed by a school or organization.
- The database is not encrypted at rest in MVP.
- The browser extension is visible to browser management tools and store review.

These limitations must be stated in the privacy disclosure rather than hidden.

## 7. Accessibility

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-A11Y-001 | MUST | All core workflows are fully keyboard operable with visible focus and no keyboard traps. | Keyboard-only test scripts. |
| BS-NFR-A11Y-002 | MUST | Controls expose accessible names, roles, states, and help text through Windows UI Automation. | Inspect.exe / Accessibility Insights review. |
| BS-NFR-A11Y-003 | MUST | Narrator can complete onboarding, create a task, start/end a session, read a summary, and restore a backup. | Narrator acceptance scripts. |
| BS-NFR-A11Y-004 | MUST | High contrast mode preserves visibility of status, focus, errors, and blocking notices. | High-contrast manual test. |
| BS-NFR-A11Y-005 | MUST | Display scaling from 100% to 200% and mixed-DPI multi-monitor setups remain usable without clipped critical controls. | DPI test matrix. |
| BS-NFR-A11Y-006 | MUST | Information is never conveyed by color alone. | Chart, status, and error review. |
| BS-NFR-A11Y-007 | MUST | Reduced-motion preference is respected by any motion introduced after references are supplied. | Motion preference test. |
| BS-NFR-A11Y-008 | MUST | Timers and session state changes are announced appropriately without flooding the screen reader. | Assistive-technology test. |
| BS-NFR-A11Y-009 | MUST | Error and recovery messages identify the problem, the affected feature, and the next action. | Content and usability review. |
| BS-NFR-A11Y-010 | SHOULD | Target conformance is WCAG 2.2 AA where applicable to a Windows desktop application. | Accessibility audit; visual contrast is blocked until references exist. |
| BS-NFR-A11Y-011 | MUST | The blocking overlay is keyboard accessible and cannot trap the user without an exit. | Overlay keyboard test. |
| BS-NFR-A11Y-012 | MUST | No essential feature depends on sound, hover, or pointer precision. | Keyboard/pointer alternatives test. |

Visual contrast, typography, motion, and target size cannot be approved until supplied references exist. The requirements remain binding once references are assessed.

## 8. Compatibility and installation

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-COM-001 | MUST | Windows 11 supported builds on x64 are the MVP target. | OS matrix test. |
| BS-NFR-COM-002 | MUST | Full mode installs and uninstalls with administrator rights; Limited mode does with standard-user rights. | Independent clean-VM tests. |
| BS-NFR-COM-003 | MUST | Both modes use the same local data path and schema. | Cross-mode data test. |
| BS-NFR-COM-004 | MUST | Chrome stable and Edge stable are supported for browser protection; other browsers are reported as unsupported. | Browser matrix. |
| BS-NFR-COM-005 | MUST | School policy that blocks extension or Native Messaging yields a usable Limited mode and an honest status. | Managed-device simulation. |
| BS-NFR-COM-006 | MUST | Full mode works after reboot, logoff/logon, fast user switching, sleep, and resume. | Lifecycle test matrix. |
| BS-NFR-COM-007 | MUST | Limited mode works when the service is not present and when the user is not an administrator. | Standard-user VM. |
| BS-NFR-COM-008 | MUST | Upgrade from Limited to Full preserves data and can fail back to Limited without data loss. | Upgrade and canceled-UAC tests. |
| BS-NFR-COM-009 | MUST | Uninstall leaves no hidden service, autostart task, named pipe, or privileged file. | Post-uninstall inspection. |
| BS-NFR-COM-010 | SHOULD | User-facing strings are externalized for future localization; English is the only MVP language. | String review. |
| BS-NFR-COM-011 | MUST | User data supports Unicode, including subject/task names and notes. | Unicode test data. |
| BS-NFR-COM-012 | MUST | Date, time, timezone, DST, and week-boundary behavior follows the domain rules. | Timezone/DST test matrix. |

## 9. Maintainability and architecture

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-MNT-001 | MUST | Domain logic is independent of WPF, Windows APIs, EF Core, and browser code. | Dependency review. |
| BS-NFR-MNT-002 | MUST | Modules have explicit boundaries and no circular dependencies. | Architecture tests. |
| BS-NFR-MNT-003 | MUST | Business rules are covered by unit tests before UI integration. | Coverage and review. |
| BS-NFR-MNT-004 | MUST | Database migrations are versioned, tested, and reversible through backup. | Migration test suite. |
| BS-NFR-MNT-005 | MUST | New dependencies require a documented justification and security review. | Dependency review checklist. |
| BS-NFR-MNT-006 | MUST | Structured logging uses correlation IDs for UI, agent, bridge, and service flows. | Log inspection. |
| BS-NFR-MNT-007 | MUST | Release artifacts have a reproducible version and build metadata. | Release checklist. |
| BS-NFR-MNT-008 | SHOULD | The codebase builds with warnings treated as errors and passes static analysis. | CI evidence. |
| BS-NFR-MNT-009 | MUST | Architectural decisions are recorded as ADRs. | ADR index review. |
| BS-NFR-MNT-010 | MUST | Full and Limited mode differences are isolated in capability/platform layers, not duplicated throughout the domain. | Code review against architecture. |

## 10. Observability and supportability

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-OBS-001 | MUST | Each component has a health state: service, agent, UI, bridge, extension, rules, database, backup. | Diagnostics test. |
| BS-NFR-OBS-002 | MUST | Logs are structured, timestamped, component-tagged, and rotated. | Log format check. |
| BS-NFR-OBS-003 | MUST | Logs and diagnostics never include prohibited personal content. | Redaction test. |
| BS-NFR-OBS-004 | MUST | The user can export a diagnostics bundle without developer assistance. | Manual support workflow. |
| BS-NFR-OBS-005 | MUST | Error messages map to documented recovery actions. | Error catalog review. |
| BS-NFR-OBS-006 | SHOULD | Support can reconstruct one request's path across the UI, agent, bridge, and service log files using the documented correlation ID and a documented trace procedure, without exposing content. Correlation-ID generation and propagation are required by BS-NFR-MNT-006. | End-to-end correlation trace test across rotated logs, including a missing-component case. |
| BS-NFR-OBS-007 | MUST | The product does not depend on a remote support service. | Offline support test. |

## 11. Release and compliance

| ID | Pri | Requirement | Verification |
|---|---|---|---|
| BS-NFR-RLS-001 | MUST | The release has a known-limitations document covering enforcement, browsers, school policy, admin rights, fullscreen apps, local data access, and uninstall. | Document review. |
| BS-NFR-RLS-002 | MUST | Browser store listings, permission justifications, privacy policy, and support contact are complete before submission. | Store checklist. |
| BS-NFR-RLS-003 | MUST | Installer and binaries are code-signed. | Signature check. |
| BS-NFR-RLS-004 | MUST | No claim of tamper resistance, anti-cheat, or guaranteed blocking appears in product copy. | Copy review. |
| BS-NFR-RLS-005 | MUST | Full and Limited installation paths are tested independently. | Separate evidence sets. |
| BS-NFR-RLS-006 | MUST | Visual approval status is accurate for every screen. | Gap log audit. |
| BS-NFR-RLS-007 | MUST | Release notes disclose deferred features and known gaps. | Release review. |

## 12. Verification rule

A non-functional requirement is verified only with recorded evidence:

- Test scenario and environment.
- Expected target.
- Measured result.
- Pass, fail, or blocked status.
- Date and build identifier.
- Known variance or limitation.

A design target, a code review, or an unmeasured estimate is not verification.
