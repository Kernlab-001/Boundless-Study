# Requirement-to-Test Traceability Matrix

**Product:** Boundless Study  
**Status:** Draft for user review; no test has been executed  
**Version:** 0.1  
**Last updated:** 2026-11-12  
**Related:** `../02-requirements/functional-requirements.md`, `../02-requirements/non-functional-requirements.md`, `test-strategy.md`, `../02-requirements/domain-business-rules.md`

## 1. Purpose and coverage

This document is the authoritative requirement-to-test map for the MVP planning baseline. It answers four questions:

1. Which test verifies this requirement?
2. Which requirement does this test verify?
3. What evidence proves the test passed?
4. Which release milestone requires it?

Coverage baseline:

- **198 functional requirements:** 188 MUST, 10 SHOULD, 0 MAY.
- **96 non-functional requirements:** 87 MUST, 9 SHOULD.
- Every functional MUST has at least one named test case.
- Every functional SHOULD has a named test case; a SHOULD may ship only with a recorded exception if its test is not passed.
- Every NFR has a measurable verification method.
- The earlier audit figure of 84 NFRs is superseded by the current baseline of 96. No NFR was dropped to match the older count.

No test in this document has been run. `NOT RUN` is not a pass. `BLOCKED` means the test cannot be executed because a required reference or environment is absent. Visual tests remain blocked until references are supplied.

## 2. Test conventions

### 2.1 Test levels

| Level | Use |
|---|---|
| Unit | Pure domain, application, validation, and economy rules. |
| Integration | Real SQLite, filesystem, IPC, native host, browser bridge, migration, backup. |
| System | Installed product across components and both modes. |
| UI | WPF workflow, state, and presentation behavior. |
| Security | ACL, origin, malformed input, privacy, signing, tamper behavior. |
| Performance | Measured targets on the reference environment. |
| Recovery | Crash, power loss, restart, migration failure, restore, corruption. |
| Compatibility | OS, browser, DPI, mode, and data-format combinations. |
| Accessibility | Keyboard, Narrator, high contrast, scaling, reduced motion, UIA. |
| Documentation | Release artifact, ADR, copy, or visual-status review. |

### 2.2 Release milestones

| Milestone | Scope |
|---|---|
| M1 | Domain and economy foundation: state machine, time, XP, ledger, levels, streak, achievements. |
| M2 | Agent, SQLite access boundary, IPC, security, logging, migrations. |
| M3 | UI core loop, settings, insights, accessibility, notifications, data surfaces. |
| M4 | Full/Limited installation, service, browser extension, upgrade, recovery, uninstall. |
| M5 | Release candidate: performance, soak, security, compatibility, store, signing, visual-status audit. |

### 2.3 Status values

| Status | Meaning |
|---|---|
| `NOT RUN` | Defined but not executed. No result is claimed. |
| `PASS` | Executed with recorded evidence meeting the expected result. |
| `FAIL` | Executed with evidence that does not meet the expected result. |
| `BLOCKED` | Cannot execute because a reference, environment, or dependency is absent. |
| `N/A — APPROVED EXCEPTION` | A SHOULD requirement has a user-approved documented exception. |

### 2.4 Table columns

`Req` lists requirement IDs; `AC` lists the matching acceptance-criteria IDs. `Mod` is the module code. `Data` is the minimum seeded test data. `Evidence` is the artifact that proves the result. A test covers multiple requirements only when its actions and expected result genuinely verify each listed requirement.

## 3. Functional test catalog

### 3.1 Cross-cutting (XCT)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-001 | BS-XCT-001; BS-SYS-027; BS-DAT-018 | AC-XCT-001; AC-SYS-027; AC-DAT-018 | XCT/SYS/DAT | System | Installed app; network disabled; seeded profile | One task; one 25-minute Balanced session; one backup | Disable networking; create task; complete session; earn XP; review insights; restore backup; capture traffic | Core loop completes offline; no outbound study data or cloud call; backup restores | Any network dependency, telemetry, or cloud data transfer | Clean-VM recording; packet capture; restore log | M4 | NOT RUN |
| BS-TEST-FUNC-002 | BS-XCT-002; BS-NFR-PRV-001; BS-NFR-PRV-003 | AC-XCT-002; BS-NFR-PRV-001; BS-NFR-PRV-003 | XCT/PRV | Security | Installed app; network capture active | Full core-loop session; seeded task, subject, notes, domains | Run onboarding, session, insights, backup with capture active | No telemetry, analytics, study data, blocked domains, app names, or diagnostics leave the machine | Any outbound telemetry or study-data transfer | Packet capture; source review; privacy checklist | M4 | NOT RUN |
| BS-TEST-FUNC-003 | BS-XCT-003; BS-SYS-028 | AC-XCT-003; AC-SYS-028 | XCT/SYS | Security | Installed Full and Limited builds | Startup entries; service; native host; extension; process list | Inspect installer, registry, startup, service, pipes, loaded modules, extension source | Components are documented and removable; no hidden autostart, kernel driver, injection, or undocumented modification | Hidden persistence, driver, or injected code | Install/runtime inspection report; file and registry diff | M4 | NOT RUN |
| BS-TEST-FUNC-004 | BS-XCT-004; BS-SYS-034 | AC-XCT-004; AC-SYS-034 | XCT/SYS | System | Full and Limited builds; controllable component failures | Missing extension; stopped agent; stopped service; policy block; overlay failure | Exercise each failure and read every status surface | UI never displays `Protected`; each surface reports its own truthful state; mode status matches active supervision | Any `Protected` claim with a failed or unverified surface | Failure-matrix recording; status screenshots after visual approval | M3 | NOT RUN |
| BS-TEST-FUNC-005 | BS-XCT-005; BS-DAT-006; BS-DAT-010; BS-DAT-011 | AC-XCT-005; AC-DAT-006; AC-DAT-010; AC-DAT-011 | XCT/DAT | System/Recovery | Seeded data; valid, corrupt, and incompatible backups | Profile; tasks; sessions; rewards; backups; logs | Export; back up; restore valid; reject corrupt; delete all; uninstall with keep and delete | Each operation ends `Succeeded`, `Cancelled`, or `Failed` with integrity result and no partial mutation; restore preserves or rejects safely; deletion removes local copies; uninstall honors data choice | Partial mutation; silent overwrite; hidden copy; unclear outcome | Operation log; before/after file inventory; checksums | M4 | NOT RUN |
| BS-TEST-FUNC-006 | BS-XCT-006; BS-NFR-A11Y-001; BS-NFR-A11Y-003 | AC-XCT-006; AC-A11Y-001; AC-A11Y-003 | XCT/A11Y | Accessibility | Windows 11; Narrator; keyboard only | Seeded profile; subject; task; backup | Complete onboarding, subject/task creation, task completion, session start/end, backup, settings changes without pointer; repeat with Narrator | All listed workflows complete; focus visible; no keyboard trap; Narrator announces controls and outcomes | Any pointer-only step, trap, or unannounced critical state | Keyboard script; Narrator recording; Accessibility Insights export | M3 | NOT RUN |
| BS-TEST-FUNC-007 | BS-XCT-007; BS-NFR-OBS-005 | AC-XCT-007; BS-NFR-OBS-005 | XCT/OBS | Integration/UI | Failure injection harness | Missing agent; locked DB; failed backup; failed restore; policy block | Trigger each documented failure; read UI and logs | Each failure has a message, affected feature, recovery action, stable code, and diagnostic event; no silent failure | Silent failure; generic message with no action | Error catalog review; UI capture; log excerpt | M3 | NOT RUN |
| BS-TEST-FUNC-008 | BS-XCT-008; BS-SYS-014 | AC-XCT-008; AC-SYS-014 | XCT/SYS | Integration/Contract | Version-negotiation harness | Incompatible IPC, extension, schema, and economy versions; unknown fields and types | Send each incompatible message or open each incompatible artifact | Rejection returns `INCOMPATIBLE_VERSION` with sender and receiver versions, the affected capability, and the required update or recovery action; no partial mutation; agent stays alive | Crash, partial write, or silent acceptance | Contract test report; protocol log | M2 | NOT RUN |
| BS-TEST-FUNC-009 | BS-XCT-009; BS-DAT-002 | AC-XCT-009; AC-DAT-002 | XCT/DAT | Integration/Security | Agent running; UI and bridge clients | Concurrent UI reads; write requests; direct-file probe | Attempt direct SQLite open from UI, service, bridge, and extension; run concurrent reads and writes through the agent | Only the agent opens the database; UI uses the UI pipe; WAL snapshots serve reads; writes serialize; no corruption | Any non-agent database open or write | Code review; process/file inspection; DB snapshot; concurrency report | M2 | NOT RUN |
| BS-TEST-FUNC-010 | BS-XCT-010; BS-NFR-RLS-006 | AC-XCT-010; BS-NFR-RLS-006 | XCT/RLS | Documentation | Gap log; screen inventory; asset folder | All 35 screens and 12 asset areas | Compare every screen and asset status with the actual reference inventory | Every screen is either approved against a supplied reference or marked `BLOCKED — AWAITING USER REFERENCES`; no false approval | Any unapproved screen marked approved | Gap-log audit; reference inventory; sign-off record | M5 | BLOCKED |

### 3.2 Onboarding and diagnostics (ONB)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-011 | BS-ONB-001 | AC-ONB-001 | ONB | UI | First run; no existing data | Local-first explanation; data path; privacy summary | Start first run; read explanation; revisit from Settings | Explanation appears before mode selection; covers local-first, data location, privacy, no mandatory account; reachable later | Explanation missing, after mode choice, or unreachable | UI recording; content checklist | M3 | NOT RUN |
| BS-TEST-FUNC-012 | BS-ONB-002; BS-SYS-001 | AC-ONB-002; AC-SYS-001 | ONB/SYS | System/Compatibility | Clean VMs: Full-capable, standard user, policy-managed, no browser | Windows version; architecture; elevation; service; Chrome/Edge; Native Messaging; existing data | Run setup detection on each environment | Distinguishes supported, unsupported, unavailable-by-policy, and not-installed for each capability | Any capability misclassified or silently assumed | Detection report; environment matrix | M4 | NOT RUN |
| BS-TEST-FUNC-013 | BS-ONB-003; BS-SYS-033 | AC-ONB-003; AC-SYS-033 | ONB/SYS | UI | Setup; Settings; user guide | Full/Limited capability rows | Compare every row of the SRS matrix with setup, Settings, and user guide | Every capability row is explained before selection and available after installation; no row contradicts the SRS | Missing, contradictory, or stronger claim | Content diff; UI capture; user-guide review | M3 | NOT RUN |
| BS-TEST-FUNC-014 | BS-ONB-004 | AC-ONB-004 | ONB | System | Clean machine; installer started | Full and Limited choices | Cancel mode selection; inspect installed components and data | No silent Full choice; cancel leaves app uninstalled or safe pre-onboarding; no privileged component installed | Silent Full install or privileged residue | Installer log; file/service inspection | M4 | NOT RUN |
| BS-TEST-FUNC-015 | BS-ONB-005; BS-SYS-002 | AC-ONB-005; AC-SYS-002 | ONB/SYS | System | Standard user; Full-capable machine | Elevation prompt; existing data | Choose Full; decline elevation; continue Limited; retry Full | One elevation request for privileged installation; explanation present; decline leaves Limited usable with no data loss; standard runtime needs no elevation | Repeated prompts, data loss, or Full claim without install | UAC recording; install log; data comparison | M4 | NOT RUN |
| BS-TEST-FUNC-016 | BS-ONB-006; BS-SYS-003; BS-SYS-030 | AC-ONB-006; AC-SYS-003; AC-SYS-030 | ONB/SYS | System | Standard-user clean VM; no service | Limited install; full session; update; uninstall | Complete Limited onboarding and all non-upgrade workflows; monitor UAC | No elevation prompt; no service installed; core loop works; update and uninstall succeed | Any elevation prompt or service dependency | UAC log; service inspection; workflow recording | M4 | NOT RUN |
| BS-TEST-FUNC-017 | BS-ONB-007 | AC-ONB-007 | ONB | UI/Integration | Chrome and Edge; extension states | Installed-connected; installed-disconnected; not-installed; policy-blocked | Walk through browser setup in each state | App distinguishes all four states and gives the correct next action | States conflated or wrong action | Browser matrix; UI capture | M4 | NOT RUN |
| BS-TEST-FUNC-018 | BS-ONB-008; BS-BRW-012; BS-SYS-031 | AC-ONB-008; AC-BRW-012; AC-SYS-031 | ONB/BRW/SYS | System | Policy-managed VM blocking extension or Native Messaging | Tasks; timer; XP; insights; browser rules | Finish Limited onboarding; start focus; use non-browser features | Onboarding completes; browser protection `Unavailable`; tasks, timers, XP, and insights work; limitation explained | Setup blocked; unrelated features disabled; repeated prompt | Policy VM recording; feature checklist | M4 | NOT RUN |
| BS-TEST-FUNC-019 | BS-ONB-009 | AC-ONB-009 | ONB/STU | UI | First run; no subjects or tasks | Valid subject; valid task; skip path | Create first subject/task; skip; edit later | First task valid; skip allowed; later edit works without blocking onboarding | Forced creation or broken skip path | UI recording; DB query | M3 | NOT RUN |
| BS-TEST-FUNC-020 | BS-ONB-010 | AC-ONB-010 | ONB | UI | First focus session not yet started | Tutorial content | Read first-session tutorial; acknowledge; start session | Explains what is blocked, what is not, how to end, and how to recover or uninstall; acknowledgement recorded | Missing topic or unrecorded acknowledgement | Tutorial capture; settings flag | M3 | NOT RUN |
| BS-TEST-FUNC-021 | BS-ONB-011 | AC-ONB-011 | ONB | UI | Onboarding in browser-setup step | Partial onboarding state | Close app; reopen; resume | Resumes at the correct step without losing completed steps | Restart at beginning or lost state | UI recording; onboarding-state query | M3 | NOT RUN |
| BS-TEST-FUNC-022 | BS-ONB-012; BS-SET-008 | AC-ONB-012; AC-SET-008 | ONB/SET | UI/Integration | Existing install; one degraded component | Mode; versions; agent; service; bridge; extension; database; backup; last error | Rerun diagnostics from Settings | All components update; degraded component identified with recovery action | Stale or missing component status | Diagnostics capture; component log | M3 | NOT RUN |

### 3.3 Dashboard (DSH)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-023 | BS-DSH-001 | AC-DSH-001 | DSH | UI/Integration | Seeded profile | Sessions with known active, elapsed, XP, level, streak | Open Today; compare every value with domain calculation | Active-study time, elapsed focus time, XP, level, and streak match; labels are clear | Any mismatch or unlabeled value | UI capture; expected/actual table | M3 | NOT RUN |
| BS-TEST-FUNC-024 | BS-DSH-002 | AC-DSH-002 | DSH/STU | UI | Tasks with and without today flag | Today task; non-today task | Open Today; open task; start focus from task | Only planned task appears; task opens; task-linked session starts | Wrong task list or broken link | UI recording; session row | M3 | NOT RUN |
| BS-TEST-FUNC-025 | BS-DSH-003 | AC-DSH-003 | DSH/FOC | UI | No active session | Profiles; durations | Start quick start without profile; without duration; with both | Missing profile or duration blocks start; valid choice opens pre-session summary | Start with missing values or skip summary | UI capture; validation message | M3 | NOT RUN |
| BS-TEST-FUNC-026 | BS-DSH-004; BS-SYS-022 | AC-DSH-004; AC-SYS-022 | DSH/SYS | UI/System | Full and Limited; controllable health failures | Mode; service; agent; browser; extension; rules | Open dashboard in each health combination | Each status maps to a domain state; no status inferred from a generic running flag | Generic “running” claim or missing surface | Status matrix; UI capture | M3 | NOT RUN |
| BS-TEST-FUNC-027 | BS-DSH-005; BS-FOC-018 | AC-DSH-005; AC-FOC-018 | DSH/FOC | UI | Seeded completed and ended sessions | Recent sessions with task, profile, active time, outcome | Open recent session; reopen summary | Summary matches session; reopening creates no ledger entry | Wrong summary or duplicate reward | UI capture; ledger diff | M3 | NOT RUN |
| BS-TEST-FUNC-028 | BS-DSH-006; BS-DSH-007 | AC-DSH-006; AC-DSH-007 | DSH/GAM | UI | No data; partial data; DB unavailable | Empty profile; partial session; stopped agent | Open dashboard in each state; launch app repeatedly | Loading, empty, partial, and error states are distinct and actionable; repeated launch changes no XP, streak, or achievement | False zero, shared error state, or progress on launch | State capture; ledger diff | M3 | NOT RUN |

### 3.4 Focus engine (FOC)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-029 | BS-FOC-001; BS-FOC-002; BS-FOC-013 | AC-FOC-001; AC-FOC-002; AC-FOC-013 | FOC | Unit/UI | No active session; configured rules | Profiles; durations; optional task; duplicate start request | Start session; inspect pre-session summary; send duplicate start | One valid session created with all values; summary appears before enforcement; duplicate start rejected or ignored; no second record | Missing summary; duplicate session; wrong values | Session table; UI recording; request log | M1/M3 | NOT RUN |
| BS-TEST-FUNC-030 | BS-FOC-003; BS-FOC-009; BS-FOC-010 | AC-FOC-003; AC-FOC-009; AC-FOC-010 | FOC | Unit/UI | Each profile; active session; configured target | Flexible, Balanced, Strict; early-exit paths; exception limits | Exercise each profile matrix; end early; grant exceptions to limits | Blocking, early exit, exception count/duration/delay, and XP rules match; exception expires and block resumes | Any matrix mismatch or exception over limit | Unit report; UI recording; exception rows | M1 | NOT RUN |
| BS-TEST-FUNC-031 | BS-FOC-004; BS-FOC-005; BS-FOC-007 | AC-FOC-004; AC-FOC-005; AC-FOC-007 | FOC | Unit/Recovery | Active session; controllable clock and input | Lock; sleep; overlay; 5-minute idle; forward/backward clock jump | Run each interruption; simulate idle; jump clock | Elapsed pauses on lock, sleep, overlay; active stops after 5 minutes and resumes on input; clock jump does not change accumulated duration | Downtime credited or timer jump | Timing report; state log | M1 | NOT RUN |
| BS-TEST-FUNC-032 | BS-FOC-006; BS-FOC-008; BS-FOC-015 | AC-FOC-006; AC-FOC-008; AC-FOC-015 | FOC | Integration/UI | Configured app and site rules; no active session | Focus start; break start; pause probe | Start Balanced/Strict; start break during focus; inspect pause controls | Guards apply on start and clear on end; break rejected during focus and lifts blocking; no user pause bypasses enforcement | Rules leak; break during focus; hidden pause | Rule/process inspection; UI capture | M3 | NOT RUN |
| BS-TEST-FUNC-033 | BS-FOC-011; BS-FOC-012; BS-SYS-019 | AC-FOC-011; AC-FOC-012; AC-SYS-019 | FOC/SYS | Recovery/Integration | Active session; kill harness | Kill at 0%, 50%, 99%; seeded summary values | Kill agent; restart; recover; inspect summary and ledger | Loses at most one checkpoint interval; no downtime credited; summary values match DB; one session and one reward | Lost checkpoint, invented time, or duplicate reward | Recovery report; DB dump; ledger dump | M4 | NOT RUN |
| BS-TEST-FUNC-034 | BS-FOC-014; BS-BRW-014; BS-FOC-016 | AC-FOC-014; AC-BRW-014; AC-FOC-016 | FOC/BRW/GAM | UI/Unit | Strict profile; site rules; extension disconnected | Site rules; extension states | Attempt Strict start; inspect session table; start then immediately end | Start blocked with explanation and recovery path; no session row before resolution; starting awards no XP | Start proceeds unverified or awards XP | UI capture; session/ledger query | M3 | NOT RUN |
| BS-TEST-FUNC-035 | BS-FOC-017 | AC-FOC-017 | FOC/UI | UI/Performance | Active session; controllable health state | Agent state change; foreground change; heartbeat stop | Change state; measure propagation; stop heartbeat | UI reflects agent state within 1 second; detection meets BS-NFR-PERF-004; heartbeat degrades within BS-NFR-PERF-006 | Stale status or false `Protected` | Timing capture; UI recording | M3 | NOT RUN |

### 3.5 Application guard (APP)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-036 | BS-APP-001; BS-APP-002; BS-APP-003; BS-SET-002 | AC-APP-001; AC-APP-002; AC-APP-003; AC-SET-002 | APP/SET | UI/Integration | Running and installed applications; file picker | Duplicate paths; case variants; system-critical process; accessibility tool | Add, edit, disable, remove; add duplicate path; attempt protected target; restart app | Targets persist and appear in pre-session summary; duplicates merge or warn; protected targets rejected with explanation | Duplicate rule, persisted protected target, or lost edit | UI recording; DB query; rejection log | M3 | NOT RUN |
| BS-TEST-FUNC-037 | BS-APP-004; BS-APP-005; BS-APP-006 | AC-APP-004; AC-APP-005; AC-APP-006 | APP/FOC | Integration/UI | Active Balanced session; blocked app | Foreground switch under and over 3 seconds; overlay actions | Bring app foreground; wait; inspect overlay; return; re-enter | Detection within declared target; no overlay under 3 seconds; overlay names app and shows remaining time; keyboard actions; timers pause | Early overlay, missing pause, or inaccessible action | Timing capture; overlay recording; timer log | M3 | NOT RUN |
| BS-TEST-FUNC-038 | BS-APP-007; BS-APP-008 | AC-APP-007; AC-APP-008 | APP | Security/Integration | Active session; blocked app; process monitor | Violation with window title and file content present | Trigger violation; inspect process, DB, logs | No termination, suspension, injection, or modification; violation stores target, start/end, duration, count; no title, URL, or content | Process modification or sensitive content stored | Process monitor report; DB/log inspection | M2 | NOT RUN |
| BS-TEST-FUNC-039 | BS-APP-009; BS-FOC-010 | AC-APP-009; AC-FOC-010 | APP/FOC | Integration | Active session; exception allowed by profile | Exception target; maximum duration; expiry | Grant exception; wait for expiry; re-enter app | Overlay suppressed for target and duration; block reapplies automatically; count and duration limits enforced | Exception over limit or block not restored | Timer log; DB exception row; UI capture | M2 | NOT RUN |
| BS-TEST-FUNC-040 | BS-APP-010; BS-APP-012 | AC-APP-010; AC-APP-012 | APP/SYS | System/UI | Full and Limited installations; elevated target; protected target; fullscreen app | Mode; healthy and stopped service; target coverage states | Compare mode status; stop the service; open pre-session summary; attempt unsupported target | Full with healthy service shows `Supervised`; Full with stopped service shows degraded supervision; Limited shows `Best effort`; targets show `Supported`, `Unsupported`, or `Unverified`; unsupported targets disclosed in onboarding and pre-session; never `Protected`; no system-wide or tamper-proof claim | False supervision, missing disclosure, or `Protected` on unsupported target | UI capture; mode/health query; onboarding review | M4 | NOT RUN |
| BS-TEST-FUNC-041 | BS-APP-011 | AC-APP-011 | APP/Recovery | Recovery | Active session; overlay-failure injection | Blocked app; forced overlay failure | Trigger failure; observe timers, notice, summary | Session pauses; enforcement failure recorded; persistent notice with recovery action; no blocked time credited | Timer continues or failure silent | Recovery log; timer report; UI capture | M4 | NOT RUN |
| BS-TEST-FUNC-042 | BS-APP-013 | AC-APP-013 | APP/FOC | Integration | Active focus; break; terminal session | Blocked app | Enter blocked app during focus, break, and after end | Overlay only during focus states; none during break or after terminal state | Overlay during break or after end | State-machine log; UI recording | M3 | NOT RUN |

### 3.6 Browser guardian (BRW)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-043 | BS-BRW-001; BS-BRW-002; BS-BRW-003; BS-SYS-015 | AC-BRW-001; AC-BRW-002; AC-BRW-003; AC-SYS-015 | BRW/SYS | System/Integration | Chrome and Edge installed; unsupported browser present | Extension ID; allowlisted origin; HKCU registration | Detect browsers; install extension through browser; connect; test mismatched ID | Chrome and Edge detected separately; unsupported browser not claimed; installation user-completed; native host uses fixed ID/origin; mismatch rejected | Silent install, unsupported claim, or origin mismatch accepted | Browser matrix; host manifest; connection log | M4 | NOT RUN |
| BS-TEST-FUNC-044 | BS-BRW-004; BS-BRW-018; BS-SET-015 | AC-BRW-004; AC-BRW-018; AC-SET-015 | BRW/SET | UI/Integration | Extension connected; controllable heartbeat | Installed; connected; policy applied; stale heartbeat; last error | Open status; stop heartbeat; run connection test | Each state distinguishable; stale heartbeat becomes `Degraded` within 30 seconds; test reports browser, extension, bridge, heartbeat, and rules separately | Conflated states or missing recovery | Status capture; timing log; test report | M4 | NOT RUN |
| BS-TEST-FUNC-045 | BS-BRW-005; BS-BRW-006; BS-BRW-007 | AC-BRW-005; AC-BRW-006; AC-BRW-007 | BRW | Integration | Active session; Chrome and Edge | Blocked domains; allowed domain; invalid domains; punycode | Start session; inspect DNR rules; navigate to block/allow/invalid cases | Rules include blocked, allowed, session ID, expiry; allow wins; valid domains accepted; invalid schemes/paths/wildcards rejected with field error | Wrong precedence, stale rule, or invalid accepted | DNR dump; browser recording; validation report | M2/M4 | NOT RUN |
| BS-TEST-FUNC-046 | BS-BRW-008; BS-BRW-009; BS-BRW-017 | AC-BRW-008; AC-BRW-009; AC-BRW-017 | BRW/Recovery | Integration/Recovery | Active session; browser restart; extension reload; agent stop | Dynamic rules with expiry | Complete, end early, abandon, start break; restart browser; reload extension; stop agent and wait | Rules removed on terminal/break; reapplied after restart/wake/update; expire when agent disappears; no stale rules | Stale block or missing reapply | Rule dump; browser recording; expiry log | M4 | NOT RUN |
| BS-TEST-FUNC-047 | BS-BRW-010; BS-BRW-011 | AC-BRW-010; AC-BRW-011 | BRW/UI | System/UI | Active session; extension/bridge/rules failures; incognito states | Allowed, NotAllowed, Unverified; internal page; guest profile; unsupported browser | Break each component; inspect status and summary; test incognito surfaces | Gap recorded; summary shows duration; incognito reported with explicit state; unsupported surfaces reported `Unverified` or `Unsupported`; no false incognito claim | Silent gap or false coverage | Gap report; UI capture; browser matrix | M4 | NOT RUN |
| BS-TEST-FUNC-048 | BS-BRW-012; BS-SYS-031 | AC-BRW-012; AC-SYS-031 | BRW/SYS | System/Compatibility | Policy-managed VM | Extension install block; Native Messaging block | Attempt setup; start focus; use non-browser features | App continues in available mode; explains exact limitation; unrelated features remain; no repeated prompt | Whole product blocked or repeated prompt | Policy VM recording; feature checklist | M4 | NOT RUN |
| BS-TEST-FUNC-049 | BS-BRW-013 | AC-BRW-013 | BRW | Security | Extension installed; network capture | Browsing history; page content; cookies; form data; URLs | Inspect permissions and source; run browsing session with capture | Only rule/status messages needed for enforcement; no history, content, cookies, form data, or URLs collected or transmitted | Any prohibited collection | Permission review; source review; packet capture | M2 | NOT RUN |
| BS-TEST-FUNC-050 | BS-BRW-014; BS-FOC-014 | AC-BRW-014; AC-FOC-014 | BRW/FOC | UI/Unit | Strict profile; site rules; no verified extension | Site rules; disconnected extension | Attempt start; resolve by install/reconnect; switch profile; remove rules | Start disabled with clear reason; no session row until resolved; every recovery path works | Start proceeds or no recovery path | UI capture; session query | M3 | NOT RUN |
| BS-TEST-FUNC-051 | BS-BRW-015; BS-BRW-016; BS-BRW-019 | AC-BRW-015; AC-BRW-016; AC-BRW-019 | BRW/Release | System/Documentation | Active session; two Chrome windows; second profile | Blocked request; session ID; release checklist | Trigger blocked page; open second window/profile; review release docs | Page names session and reason; no browsing history revealed; both windows/profiles apply policy; docs contain fixed ID, origin, store channel, permissions | Missing page, inconsistent policy, or release gap | Browser capture; policy dump; release checklist | M4/M5 | NOT RUN |

### 3.7 Study manager (STU)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-052 | BS-STU-001; BS-STU-008 | AC-STU-001; AC-STU-008 | STU | UI/Integration | Seeded subjects and tasks | Archived subject; deleted task; historical session | Create, rename, describe, archive; soft-delete task; open history | Archived subject hidden from new-task selection but readable; deleted task history remains readable | Lost history or archived subject selectable | UI capture; DB query | M3 | NOT RUN |
| BS-TEST-FUNC-053 | BS-STU-002; BS-STU-003; BS-STU-004 | AC-STU-002; AC-STU-003; AC-STU-004 | STU | Unit/UI | Empty task list | Valid and invalid titles; estimates 4, 5, 600, 605; status transitions | Create/edit task; test boundaries; attempt invalid transitions | All fields persist and validate; title required ≤200; estimate 5–600 in 5-minute steps; valid status transitions; invalid rejected or normalized | Invalid accepted or history corrupted | UI capture; validation report; DB row | M1/M3 | NOT RUN |
| BS-TEST-FUNC-054 | BS-STU-005; BS-STU-011 | AC-STU-005; AC-STU-011 | STU | UI | Tasks with and without today flag | Manual today selection | Add/remove today; wait; inspect auto scheduling | Today changes only by user action; no task receives automatic planned time | Automatic change or auto-scheduling claim | UI recording; DB query | M3 | NOT RUN |
| BS-TEST-FUNC-055 | BS-STU-006; BS-STU-007; BS-GAM-009 | AC-STU-006; AC-STU-007; AC-GAM-009 | STU/GAM | Integration/Unit | Task linked to session; task Done | Rename/delete task; reopen and re-complete | Start task-linked session; rename/delete; complete twice | Session stores task/subject snapshots; rename/delete does not break history; task XP 25 once; reopen/re-complete creates no second entry | Broken history or duplicate task XP | DB dump; ledger dump; UI capture | M1/M3 | NOT RUN |
| BS-TEST-FUNC-056 | BS-STU-009; BS-STU-012 | AC-STU-009; AC-STU-012 | STU/A11Y | UI/Accessibility | Seeded tasks and subjects | Status, subject, due date, priority, today flag; bulk archive | Filter/sort combinations; keyboard-only bulk archive and archive review | Filter and sort results are deterministic for each combination; keyboard-only filtering, sorting, bulk archive, and archive review complete with visible focus and screen-reader labels | Non-deterministic result or inaccessible control | UI automation; keyboard script; Accessibility Insights | M3 | NOT RUN |
| BS-TEST-FUNC-057 | BS-STU-010 | AC-STU-010 | STU | UI | Empty, loading, validation, DB-error states | Invalid input; stopped agent | Open each state; submit invalid; stop agent | States distinct and actionable; no false zero; unsaved input not silently dropped | Shared state or lost input | UI capture; state log | M3 | NOT RUN |

### 3.8 Gamification and progression (GAM)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-058 | BS-GAM-001; BS-GAM-002 | AC-GAM-001; AC-GAM-002 | GAM | Unit | Seeded profile; no prior session XP | `ECONOMY-1` vectors TV-01..08, TV-15, TV-19 | Run each vector; inspect computed and awarded XP; inspect cap | Exact formula and multipliers; 10-minute minimum; 300/day cap with partial remaining balance; no carry forward; task XP separate | Any value mismatch or cap breach | Unit report; ledger rows; vector table | M1 | NOT RUN |
| BS-TEST-FUNC-059 | BS-GAM-003; BS-GAM-004 | AC-GAM-003; AC-GAM-004 | GAM | Unit | Seeded totals and dates | TV-16 boundaries; same-day, consecutive-day, missed-day sessions | Run level boundaries; run streak scenarios | Levels 1/1/2/49/50/50/50; total XP recorded above 25,000; same-day no double; missed day resets; longest retained | Wrong boundary or streak | Unit report; derived values | M1 | NOT RUN |
| BS-TEST-FUNC-060 | BS-GAM-005; BS-GAM-006 | AC-GAM-005; AC-GAM-006 | GAM | Unit/Integration | Achievement definitions; ledger | Five thresholds; duplicate unlock; replay | Unlock each achievement; repeat; replay event | Each unlocks once at threshold; fixed XP; displays criteria; duplicate/replay creates no second entry | Premature unlock, duplicate, or missing display | Unit report; ledger rows; UI capture | M1 | NOT RUN |
| BS-TEST-FUNC-061 | BS-GAM-007; BS-GAM-013 | AC-GAM-007; AC-GAM-013 | GAM | Integration/Recovery | Seeded session; fault injection; ledger | Completion with reward; version change; forced failure | Inject failure before and after commit; retry; inspect version | Atomic completion/reward; retry succeeds once; every entry stores `ECONOMY-1`; later version does not recalculate history | Partial commit, duplicate, or recalculated history | Fault-injection report; ledger dump | M1/M2 | NOT RUN |
| BS-TEST-FUNC-062 | BS-GAM-008; BS-GAM-011 | AC-GAM-008; AC-GAM-011 | GAM/UI | UI/Integration | Fresh profile | App launch; onboarding; dashboard; settings; token probe | Repeat each passive action; search for token UI | No XP, streak, or achievement change; no token award, token balance, shop, or theme surface reachable | Passive reward or token UI | Ledger diff; UI inventory | M3 | NOT RUN |
| BS-TEST-FUNC-063 | BS-GAM-009; BS-STU-007 | AC-GAM-009; AC-STU-007 | GAM/STU | Unit/Integration | Valid task; no prior completion | First Done; reopen; Done again; delete/recreate | Complete task; repeat; delete/recreate | 25 XP once; duplicate completion no award; delete/recreate creates a new task identity with its own one-time award | Duplicate or missing award | Ledger dump; task rows | M1 | NOT RUN |
| BS-TEST-FUNC-064 | BS-GAM-010; BS-NOT-004 | AC-GAM-010; AC-NOT-004 | GAM/NOT | UI/Integration | Active session; achievement/level pending | Terminal session; overlay; countdown | Trigger achievement and level-up during session; observe presentation | Queued until terminal; shown before next session; never over overlay or confirmation countdown | Interrupts focus or appears over overlay | UI recording; event log | M3 | NOT RUN |
| BS-TEST-FUNC-065 | BS-GAM-012; BS-DAT-017 | AC-GAM-012; AC-DAT-017 | GAM/DAT | Integration/Recovery | Database with seeded `TOKEN` ledger entry | Token entry; upgrade; backup; restore; migration | Run each path; inspect token rows and UI | Token entry preserved through upgrade, backup, restore, migration; no token UI in MVP | Token loss or token UI | DB dump; migration log; UI inventory | M4 | NOT RUN |
| BS-TEST-FUNC-066 | BS-GAM-014; BS-GAM-015; BS-GAM-016; BS-GAM-017 | AC-GAM-014; AC-GAM-015; AC-GAM-016; AC-GAM-017 | GAM | Unit/UI/Integration | Seeded sessions across timezones and restart | Cross-midnight; DST; timezone change; restart | Run date scenarios; attempt ledger edit/delete; restart UI | Local date rules hold; ledger immutable; XP/level/streak/achievements rebuild; progression explanation reachable | Wrong date, editable ledger, lost state, or missing explanation | Unit report; DB constraints; UI capture | M1/M3 | NOT RUN |
| BS-TEST-FUNC-115 | BS-GAM-018 | AC-GAM-018 | GAM | Unit/Integration | Seeded achievement catalog; one existing unlock | ACH-001 definition; attempted CriteriaType/Threshold change; silent overwrite; deletion; code reuse | Audit catalog; attempt criteria mutation, silent overwrite, deletion, and code reuse; inspect historical unlock | One immutable eligibility definition per AchievementCode; criteria change, silent overwrite, deletion, and code reuse are rejected; historical unlock retains ACH-001 and resolves to the original definition; no duplicate reward | Criteria overwrite, deleted definition, reused code, or reinterpreted unlock | Catalog audit; DB dump; ledger dump | M1 | NOT RUN |

### 3.9 Insights (INS)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-067 | BS-INS-001; BS-INS-002; BS-INS-009 | AC-INS-001; AC-INS-002; AC-INS-009 | INS | Integration | Seeded cross-midnight and timezone sessions | Today, week, 30 days; active, elapsed, break, completed, ended early, gaps | Open each range; compare totals; run recovery/migration/timezone reconciliation | Range boundaries and metrics correct; labels and definitions traceable; XP reconciles with ledger; time reconciles with sessions | Off-by-one boundary or reconciliation failure | Query report; ledger/session diff; UI capture | M3 | NOT RUN |
| BS-TEST-FUNC-068 | BS-INS-003; BS-INS-004 | AC-INS-003; AC-INS-004 | INS | Integration/UI | Sessions with subjects and tasks | Subject split; session history fields | Open subject split; open history row | Subject totals reconcile with sessions; history shows date, task, subject, profile, planned, active, outcome, protection, XP; row opens summary | Mismatch or missing field | UI capture; query result | M3 | NOT RUN |
| BS-TEST-FUNC-069 | BS-INS-005; BS-INS-006 | AC-INS-005; AC-INS-006 | INS | UI | Ranges with 0, 4, 5, and 20 sessions; subject with 2 and 3 sessions | Metric definitions; insufficient data | Open definitions; inspect percentages at thresholds | Definitions explain idle and gap treatment; <5 sessions suppress percentage and show counts; <3 subject sessions suppress subject percentage and state why | Percentage with insufficient denominator or missing definition | UI capture; threshold table | M3 | NOT RUN |
| BS-TEST-FUNC-070 | BS-INS-007; BS-INS-008 | AC-INS-007; AC-INS-008 | INS/A11Y | UI/Accessibility | Charts with data | Accessible table; text summary; score/social probe | Navigate charts without color or pointer; search for score/rank/leaderboard | All data reachable; no color-only meaning; no composite score, rank, leaderboard, or peer comparison | Inaccessible chart or social/score feature | Accessibility report; UI inventory | M3 | NOT RUN |
| BS-TEST-FUNC-071 | BS-INS-010 | AC-INS-010 | INS/Performance | Performance/UI | 100,000 sessions | Long-running query; 200 ms threshold | Open insights; measure progress state; test keyboard responsiveness; measure p95 | Progress state appears if query exceeds 200 ms; keyboard remains responsive; query meets BS-NFR-PERF-008 | Frozen UI or missed target | Timing capture; UI recording | M3/M5 | NOT RUN |

### 3.10 Data management (DAT)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-072 | BS-DAT-001; BS-DAT-002; BS-XCT-009 | AC-DAT-001; AC-DAT-002; AC-XCT-009 | DAT/XCT | Integration/Security | Agent running; both modes installed | Default path; WAL DB; UI read and write requests | Verify path; probe direct DB open from UI/service/bridge/extension; run concurrent reads/writes | Same path and schema; only agent opens DB; UI uses UI pipe; WAL snapshots; writes serialized; no corruption | Direct open, wrong path, or corruption | Path check; process inspection; DB report | M2 | NOT RUN |
| BS-TEST-FUNC-073 | BS-DAT-003; BS-DAT-004 | AC-DAT-003; AC-DAT-004 | DAT | Integration/Recovery | Seeded database; migration harness | Schema version; automatic backup schedule; retention data | Run migration; force failure; run automatic backups; inspect retention | Migration versioned and preceded by backup; failure leaves readable pre-migration backup; seven recent plus latest pre-migration retained; old backups removed only after verified new backup | No backup, lost DB, or premature deletion | Migration log; backup inventory; integrity report | M2/M4 | NOT RUN |
| BS-TEST-FUNC-074 | BS-DAT-005; BS-DAT-006; BS-DAT-007 | AC-DAT-005; AC-DAT-006; AC-DAT-007 | DAT | Recovery | Valid, corrupt, truncated, incompatible backups | Manifest; schema; app version; checksum; DB snapshot | Create manual backup; restore each type; run startup/backup integrity check | Manifest/checksum validate; restore creates safety backup, verifies integrity, rejects bad input without corrupting current data; integrity results recorded | Bad backup accepted or current data corrupted | Backup manifest; restore log; integrity output | M4 | NOT RUN |
| BS-TEST-FUNC-075 | BS-DAT-008; BS-DAT-015 | AC-DAT-008; AC-DAT-015 | DAT/Recovery | Recovery | Corrupt database; schema mismatch | Quarantine path; old schema | Open app; choose restore/quarantine; attempt UI connect | Corrupt DB quarantined with diagnostic; no silent history deletion; schema mismatch produces recovery/update state, not partial load | Silent deletion or partial load | Recovery report; quarantine file; UI state | M4 | NOT RUN |
| BS-TEST-FUNC-076 | BS-DAT-009 | AC-DAT-009 | DAT | Integration/UI | Seeded subjects, tasks, sessions, rewards, achievements | Export ZIP; manifest; six JSON files | Export; unzip; validate schemas; verify checksums; open without app | ZIP contains `manifest.json`, `subjects.json`, `tasks.json`, `sessions.json`, `rewards.json`, `achievements.json`; manifest records schema/app/economy versions, export time, counts, SHA-256 per file; files valid and readable | Missing file, invalid schema, or checksum mismatch | ZIP inventory; schema validation report; checksum output | M3 | NOT RUN |
| BS-TEST-FUNC-077 | BS-DAT-010; BS-DAT-011 | AC-DAT-010; AC-DAT-011 | DAT | System/Recovery | Active session; backups; logs; settings | Data inventory; uninstall choices | Delete all; restart; uninstall keep; uninstall delete | Delete stops sessions and removes DB, backups, logs, settings; restart creates clean profile; uninstall honors keep/delete and explains extension removal | Hidden copy or ignored choice | File inventory; uninstall log; restart state | M4 | NOT RUN |
| BS-TEST-FUNC-078 | BS-DAT-012; BS-DAT-013; BS-DAT-014 | AC-DAT-012; AC-DAT-013; AC-DAT-014 | DAT/Security | Security | Seeded sensitive strings; second standard user | URLs; titles; task notes; names; logs; diagnostics | Export diagnostics; inspect logs; attempt access as second user | No URLs, titles, notes, or names in bundle or logs; logs rotate; ACLs deny the second user | Sensitive content or cross-user access | Redaction report; ACL dump; log inspection | M2 | NOT RUN |
| BS-TEST-FUNC-079 | BS-DAT-016; BS-SYS-020 | AC-DAT-016; AC-SYS-020 | DAT/SYS | System/Recovery | Seeded Limited profile and database | Row counts; keys; fields; relationships; idempotency keys; XP/level/streak | Upgrade to Full; compare before/after | Same profile ID and DB path; row counts, primary keys, non-derived fields, relationships, idempotency keys, and derived totals match; no second profile | Data loss, second profile, or changed idempotency | Before/after dump; comparison report; upgrade log | M4 | NOT RUN |
| BS-TEST-FUNC-080 | BS-DAT-017; BS-GAM-012 | AC-DAT-017; AC-GAM-012 | DAT/GAM | Integration/Recovery | Seeded `TOKEN` ledger entry | Token row; upgrade; backup; restore; migration | Run each path; inspect token row and UI | Token entry preserved; no token UI exposed in MVP | Token loss or token UI | DB dump; UI inventory | M4 | NOT RUN |
| BS-TEST-FUNC-081 | BS-DAT-018; BS-SYS-027; BS-XCT-001 | AC-DAT-018; AC-SYS-027; AC-XCT-001 | DAT/SYS/XCT | System | Network disabled; capture active | Full core-loop data | Run core loop; inspect outbound traffic | No cloud backup, sync, or study-data network call; core loop works offline | Any cloud or study-data call | Packet capture; offline recording | M4 | NOT RUN |

### 3.11 Notifications (NOT)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-082 | BS-NOT-001; BS-NOT-002 | AC-NOT-001; AC-NOT-002 | NOT | UI/Integration | Completed session; ended-early session; break | Outcome; active time; preferences | Complete session; end early; finish break; repeat | One notification per event; outcome and active time; does not block summary; break notification once; preferences respected | Duplicate, missing, or blocking notification | Notification capture; event log | M3 | NOT RUN |
| BS-TEST-FUNC-083 | BS-NOT-003; BS-NOT-005 | AC-NOT-003; AC-NOT-005 | NOT/UI | UI/Integration | Active session; backup failure | Protection loss; agent/service failure; overlay failure; backup failure | Trigger each failure; dismiss/retry; resolve | Persistent top banner with severity and recovery action; critical protection failure remains until resolved or session ends; backup warning until resolved/dismissed; status unhealthy | Transient or dismissible critical banner | UI capture; state log | M3 | NOT RUN |
| BS-TEST-FUNC-084 | BS-NOT-004; BS-GAM-010 | AC-NOT-004; AC-GAM-010 | NOT/GAM | UI | Active session; pending achievement/level | Overlay; confirmation countdown; terminal state | Trigger feedback in each state; observe queue | Feedback not shown over overlay or countdown; queued until terminal; presented before next session | Feedback interrupts or is lost | UI recording; queue log | M3 | NOT RUN |
| BS-TEST-FUNC-085 | BS-NOT-006; BS-SET-004 | AC-NOT-006; AC-SET-004 | NOT/SET | UI | Notification settings | Categories; global quiet hours; per-category override | Configure categories; global quiet hours; per-category override; trigger critical and non-critical | Preferences persist; disabled non-critical categories silent; global quiet hours suppress non-critical; per-category override only that category; critical protection remains in-app | Wrong category suppressed or critical hidden | UI capture; notification log | M3 | NOT RUN |
| BS-TEST-FUNC-086 | BS-NOT-007; BS-NOT-008; BS-NOT-009; BS-NOT-010 | AC-NOT-007; AC-NOT-008; AC-NOT-009; AC-NOT-010 | NOT | UI/System/Accessibility | Full and Limited installs; repeated starts/stops | Notification controls; toast registration | Repeat starts/stops; keyboard/screen-reader use; inspect notification surfaces | Rate-limited, no duplicate/nagging; keyboard and screen-reader accessible; session/protection toasts appear in both modes; no inbox or notification center | Spam, inaccessible notice, missing toast, or inbox | Notification log; Accessibility Insights; UI inventory | M3/M4 | NOT RUN |

### 3.12 System integration (SYS)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-087 | BS-SYS-001; BS-ONB-002 | AC-SYS-001; AC-ONB-002 | SYS/ONB | System/Compatibility | Clean VMs with varied capabilities | Elevation; admin eligibility; service; browser policy; extension | Run setup detection on each VM | Distinguishes elevation, admin eligibility, service availability, browser policy, and extension availability; no silent assumption | Misclassified or missing capability | Detection matrix; environment log | M4 | NOT RUN |
| BS-TEST-FUNC-088 | BS-SYS-002; BS-SYS-004; BS-SYS-009 | AC-SYS-002; AC-SYS-004; AC-SYS-009 | SYS | System/Recovery | Clean Full-capable VM | Service; agent; logon/logoff; crash; fast user switching | Clean install; start/stop/restart service; kill agent; switch users | One elevation for install; service starts at boot, supervises agent, records health; restart attempts at logon/logoff/crash/FUS; failure visible | Missing service, no recovery, or repeated elevation | Install log; service log; recovery report | M4 | NOT RUN |
| BS-TEST-FUNC-089 | BS-SYS-003; BS-SYS-010; BS-SYS-030 | AC-SYS-003; AC-SYS-010; AC-SYS-030 | SYS | System/Compatibility | Standard-user VM; no service | Limited install; startup registration; update; uninstall | Complete all non-upgrade workflows; disable/enable startup; monitor UAC | No elevation; no service; startup visible and reversible; update/uninstall succeed; zero prompts | Elevation prompt or service dependency | UAC log; startup inspection; workflow recording | M4 | NOT RUN |
| BS-TEST-FUNC-090 | BS-SYS-005; BS-SYS-035 | AC-SYS-005; AC-SYS-035 | SYS | Security/System | Full install; process monitor | Service; agent restart task; pipes | Inspect service behavior; trigger restart; inspect persistence | Service does not write study data, show UI, terminate arbitrary processes, or install hidden persistence; restart uses documented removable mechanism | Service overreach or hidden persistence | Process/registry inspection; service log | M2/M4 | NOT RUN |
| BS-TEST-FUNC-091 | BS-SYS-006; BS-SYS-007; BS-SYS-008 | AC-SYS-006; AC-SYS-007; AC-SYS-008 | SYS | Integration/System | Agent running; UI closed; Limited install | Session state; second agent launch; no service | Close UI during session; launch second agent; run core loop without service | Closing UI does not stop session; agent owns state/DB; second agent exits or hands off; Limited core loop passes | Session stops, duplicate agent, or service dependency | Process list; session log; Limited test report | M2/M4 | NOT RUN |
| BS-TEST-FUNC-092 | BS-SYS-011; BS-SYS-013; BS-SYS-014 | AC-SYS-011; AC-SYS-013; AC-SYS-014 | SYS/Security | Integration/Security | Agent pipe server; test clients | Request IDs; timeouts; size limits; ACLs; malformed frames | Connect as other user; unapproved role; send malformed/oversized/slow/unknown-version frames | Wrong user/role rejected; malformed/oversized/slow rejected without crash; request IDs and timeouts honored; no mutation | Unauthorized access or agent crash | Security report; protocol log | M2 | NOT RUN |
| BS-TEST-FUNC-093 | BS-SYS-012; BS-SYS-015 | AC-SYS-012; AC-SYS-015 | SYS/BRW | Integration | Bridge; Chrome and Edge | Native host manifest; extension ID; origin | Restart bridge; connect expected host; attempt mismatched origin | Bridge stateless and reconnects; expected host launches; mismatched origin fails safely; no study data stored in bridge | State in bridge or origin bypass | Bridge inspection; connection log | M2/M4 | NOT RUN |
| BS-TEST-FUNC-094 | BS-SYS-016; BS-SYS-017; BS-SYS-018 | AC-SYS-016; AC-SYS-017; AC-SYS-018 | SYS | System/Recovery | Two Windows users; startup order combinations | Lock; sleep; resume; shutdown; reboot; FUS | Open UI before agent; browser before agent; run lifecycle; switch users | Reconnects without data loss; downtime not credited; sessions recover/abandon correctly; users isolated | Data loss, merged profiles, or wrong state | Lifecycle log; per-user DB inspection | M4 | NOT RUN |
| BS-TEST-FUNC-095 | BS-SYS-019; BS-SYS-021 | AC-SYS-019; AC-SYS-021 | SYS/Recovery | Recovery | Active session; upgrade flow | Agent crash; application crash; canceled UAC; forced installer failure | Kill agent/app; retry; cancel upgrade; force failure | One session and one ledger entry; failed/canceled upgrade leaves Limited usable with no data change | Duplicate reward or broken Limited mode | Recovery report; data comparison | M4 | NOT RUN |
| BS-TEST-FUNC-096 | BS-SYS-020; BS-DAT-016 | AC-SYS-020; AC-DAT-016 | SYS/DAT | System/Recovery | Seeded Limited profile | Tasks; sessions; rewards; achievements; profile | Upgrade to Full; compare logical data | Privileged components installed; profile and data preserved; no second profile/reset | Data loss or second profile | Upgrade log; before/after diff | M4 | NOT RUN |
| BS-TEST-FUNC-097 | BS-SYS-022; BS-SYS-023; BS-SYS-034 | AC-SYS-022; AC-SYS-023; AC-SYS-034 | SYS/UI | System/UI | Full and Limited; stopped service | Service; agent; bridge; extension; rules | Stop service; open UI; inspect every health surface | Distinct health states; stopped service shows degraded supervision; existing sessions truthful; no single running flag; no stronger claim | False health or supervision claim | UI capture; health query | M3/M4 | NOT RUN |
| BS-TEST-FUNC-098 | BS-SYS-024; BS-SYS-025 | AC-SYS-024; AC-SYS-025 | SYS | System/Documentation | Full and Limited installs; diagnostics data | Service; startup; pipes; files; logs | Uninstall both modes; export diagnostics; inspect leftovers | No hidden service, task, pipe, or privileged file; diagnostics includes versions, mode, health, extension, service, logs, schema, no personal content | Leftover component or privacy leak | Post-uninstall inspection; bundle review | M4 | NOT RUN |
| BS-TEST-FUNC-099 | BS-SYS-026; BS-SYS-028 | AC-SYS-026; AC-SYS-028 | SYS/Security | Security/Integration | Active session; multi-component flow | Session; IPC; browser; service events | Inspect structured logs; search for sensitive content; inspect modules | Logs rotate and correlate events; no sensitive content; no kernel driver, global injection, or undocumented modification | Sensitive log or prohibited mechanism | Log review; module inspection | M2/M4 | NOT RUN |
| BS-TEST-FUNC-100 | BS-SYS-027; BS-XCT-001; BS-DAT-018 | AC-SYS-027; AC-XCT-001; AC-DAT-018 | SYS/XCT/DAT | System | Network disabled; capture active | Full core-loop data | Run core loop; inspect outbound traffic | No network request required; no study data transferred | Network dependency or transfer | Packet capture; offline recording | M4 | NOT RUN |
| BS-TEST-FUNC-101 | BS-SYS-029; BS-SYS-033 | AC-SYS-029; AC-SYS-033 | SYS/Release | Documentation/System | Signed release candidate | Binaries; installer; native host; capability matrix | Verify signatures; inspect version; open capability matrix in setup, Settings, docs | All artifacts signed and versioned; matrix available in all three places and matches SRS | Unsigned artifact or missing matrix | Signature report; content diff | M5 | NOT RUN |
| BS-TEST-FUNC-102 | BS-SYS-031; BS-BRW-012 | AC-SYS-031; AC-BRW-012 | SYS/BRW | System/Compatibility | Policy-managed VM | Extension/Native Messaging block | Start app; use tasks, timers, XP, insights | Policy failure surfaced; unrelated features remain functional; no repeated prompt | Whole product disabled | Policy VM recording; feature checklist | M4 | NOT RUN |
| BS-TEST-FUNC-103 | BS-SYS-032; BS-DAT-001 | AC-SYS-032; AC-DAT-001 | SYS/DAT | Compatibility/Integration | Database created in Limited; Full install available | Same profile; same schema | Open Limited DB in Full; open Full DB in Limited | Same local account, path, schema, and profile; no migration or reset on mode open | Schema divergence or second profile | DB inspection; cross-mode report | M4 | NOT RUN |

### 3.13 Settings and accessibility (SET)

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-FUNC-104 | BS-SET-001; BS-SET-010 | AC-SET-001; AC-SET-010 | SET | UI | Existing history; both modes | Default profile; durations; mode health | Change defaults; start new session; open Installation Mode | New sessions use defaults; history unchanged; mode shows Full/Limited, capabilities, health, upgrade path; upgrade only when eligible; denial leaves Limited intact | History rewrite or false mode | UI capture; session query | M3/M4 | NOT RUN |
| BS-TEST-FUNC-105 | BS-SET-002; BS-SET-003 | AC-SET-002; AC-SET-003 | SET | UI | Configured blocklists | Blocked app/site; allowed site; duplicate across lists | Add/edit/disable/remove; create allow+block overlap; start session | Changes persist and apply next session; allow wins; precedence explained | Wrong precedence or lost change | UI capture; rule dump | M3 | NOT RUN |
| BS-TEST-FUNC-106 | BS-SET-004; BS-NOT-006 | AC-SET-004; AC-NOT-006 | SET/NOT | UI | Notification settings | Categories; global quiet hours; per-category overrides | Configure and trigger each category; critical failure during quiet hours | Basic categories/global quiet hours work; per-category override only that category; critical protection remains in-app; preferences persist | Wrong suppression or hidden critical | Notification log; UI capture | M3 | NOT RUN |
| BS-TEST-FUNC-107 | BS-SET-005; BS-SYS-010 | AC-SET-005; AC-SYS-010 | SET/SYS | UI/System | Limited install; Full install | Startup toggle; service startup view | Disable/enable Limited startup; inspect Full service startup | Limited startup visible and reversible; Full service startup view accurate; no hidden autostart | Hidden or broken startup | Startup inspection; UI capture | M4 | NOT RUN |
| BS-TEST-FUNC-108 | BS-SET-006 | AC-SET-006 | SET | UI | Seeded data; agent available | Backup; restore; export; integrity; delete controls | Open Data & Backup; invoke each action; inspect confirmation and outcome surfaces | Every documented action is present, names its scope, requires confirmation where destructive, and shows progress plus `Succeeded`, `Cancelled`, or `Failed` with a next step; no action silently overwrites or discards data | Missing action, missing confirmation, or silent overwrite | UI capture; operation log | M3 | NOT RUN |
| BS-TEST-FUNC-109 | BS-SET-007; BS-NFR-A11Y-004; BS-NFR-A11Y-005; BS-NFR-A11Y-007 | AC-SET-007; AC-A11Y-004; AC-A11Y-005; AC-A11Y-007 | SET/A11Y | Accessibility | Windows 11; 100% and 200% scaling; mixed DPI; reduced motion | High contrast; text scaling; focus visibility; timer announcement; chart table | Change Windows settings; use app-specific options; inspect behavior | High contrast, 100–200% scaling, keyboard, Narrator, and reduced motion pass; Windows changes propagate; unsupported setting documented with fallback | Clipped control or ignored preference | Accessibility report; scaling matrix | M3/M5 | NOT RUN |
| BS-TEST-FUNC-110 | BS-SET-008; BS-SET-009 | AC-SET-008; AC-SET-009 | SET | UI | Existing install | Mode; versions; health; licenses; support path | Open Diagnostics and About | Diagnostics updates all components; About shows product/schema versions, privacy summary, licenses, support/export path matching build | Stale or mismatched value | UI capture; build metadata | M3 | NOT RUN |
| BS-TEST-FUNC-111 | BS-SET-011; BS-SET-012 | AC-SET-011; AC-SET-012 | SET | UI/Integration | Active session; both modes | Blocklist edit; exception | Open recovery/uninstall/extension guidance; edit blocklist mid-session; grant exception | Guidance accurate for current mode; mid-session edits apply next session; temporary exception still works; Strict not weakened | Wrong guidance or mid-session weakening | UI capture; rule diff | M3 | NOT RUN |
| BS-TEST-FUNC-112 | BS-SET-013; BS-SET-014 | AC-SET-013; AC-SET-014 | SET | UI | Seeded data; existing onboarding | Onboarding flags; theme probe | Reset onboarding; inspect tasks/sessions/ledger; search for theme controls | Reset affects onboarding only; study data unchanged; no theme/color customization controls | Data loss or invented theme UI | UI capture; DB diff | M3 | NOT RUN |
| BS-TEST-FUNC-113 | BS-SET-015; BS-BRW-018 | AC-SET-015; AC-BRW-018 | SET/BRW | Integration | Browser/extension/bridge/rules states | Connection failure combinations | Run connection test for each combination | Reports extension, bridge, heartbeat, and rule status separately; each failure has recovery action | Conflated status or no action | Test report; UI capture | M4 | NOT RUN |
| BS-TEST-FUNC-114 | BS-SET-016 | AC-SET-016 | SET | UI/Documentation | Full and Limited modes; 5 non-developer participants | Mode status copy; three comprehension questions | Run moderated comprehension test | At least 4 of 5 correctly answer: active mode, supervision present, what works if service unavailable | Less than 4 correct or misleading copy | Test script; response record; copy review | M3 | NOT RUN |

## 4. Non-functional verification catalog

NFRs do not have separate acceptance-criteria IDs. The NFR ID is the acceptance criterion and carries its verification method. The `AC` column repeats the NFR ID to preserve the same 13-field test record. A target is not a pass until measured on the reference environment and recorded with date and build.

### 4.1 Reliability and data integrity

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-001 | BS-NFR-REL-001 | BS-NFR-REL-001 | REL | Recovery | Active session; crash harness | Session at start, middle, end; ledger | Kill process, cut power, reboot; reopen; integrity check | Database not corrupted; no duplicate reward; state is consistent | Corruption or duplicate | Integrity report; DB dump | M4 | NOT RUN |
| BS-TEST-NFR-002 | BS-NFR-REL-002 | BS-NFR-REL-002 | REL | Recovery | Active session; kill harness | Kill at 0%, 50%, 99%; checkpoint timestamps | Kill; restart; compare elapsed/active; inspect reward | Lost data ≤ one checkpoint interval; no downtime credit; no invented completion | Lost interval exceeded or time invented | Recovery report; checkpoint diff | M4 | NOT RUN |
| BS-TEST-NFR-003 | BS-NFR-REL-003 | BS-NFR-REL-003 | REL | Integration/Recovery | Fault injection around ledger and session transactions | Completion; task; achievement | Fail transaction at each point; inspect source and ledger | No completed source without reward; no reward without source | Partial commit | Fault-injection report; DB dump | M2 | NOT RUN |
| BS-TEST-NFR-004 | BS-NFR-REL-004 | BS-NFR-REL-004 | REL | Recovery | Migration harness; seeded DB | Fail at each migration step | Force failure; open original; restore | Pre-migration DB readable and recoverable; backup valid | Unreadable DB or lost backup | Migration log; restore report | M4 | NOT RUN |
| BS-TEST-NFR-005 | BS-NFR-REL-005 | BS-NFR-REL-005 | REL | Integration | Agent running; lock holder; UI client | Long write lock; concurrent reads; timeout; cancellation | Hold lock; issue reads/writes; cancel; wait | Bounded retries; visible wait/error; readers agent-mediated; timeout/cancel honored; no direct DB fallback | Unbounded wait, crash, or direct access | Lock test report; IPC log | M2 | NOT RUN |
| BS-TEST-NFR-006 | BS-NFR-REL-006 | BS-NFR-REL-006 | REL | Recovery/System | Full mode; service and agent | Stop agent; stop service; deny restart | Trigger each failure; inspect UI and logs | Documented restart attempt; failure visible; no silent failure | Silent failure or false recovery | Service log; UI capture | M4 | NOT RUN |
| BS-TEST-NFR-007 | BS-NFR-REL-007 | BS-NFR-REL-007 | REL | Compatibility | Standard user; no service; policy-blocked browser | Tasks; session; policy block | Run Limited core loop; block extension | Limited remains usable; browser protection available or `Unavailable` | Feature loss or blocked app | Limited test report; policy VM recording | M4 | NOT RUN |
| BS-TEST-NFR-008 | BS-NFR-REL-008 | BS-NFR-REL-008 | REL | Performance/Recovery | Eight-hour run; repeated sessions; sleep/resume; browser reconnect | Automated soak data | Run eight hours; capture memory/errors; inspect data | No unbounded memory growth; no data loss; recovery paths hold | Leak, crash, or data loss | Soak metrics; error log | M5 | NOT RUN |
| BS-TEST-NFR-009 | BS-NFR-REL-009 | BS-NFR-REL-009 | REL | Recovery | Valid, truncated, corrupt, incompatible backups | Manifest; checksum; schema | Restore each type; inspect before/after | Checksum, schema, and integrity checked before and after; bad backup rejected | Bad backup accepted or current data damaged | Restore report; checksum output | M4 | NOT RUN |
| BS-TEST-NFR-010 | BS-NFR-REL-010 | BS-NFR-REL-010 | REL | Recovery | Corrupt database | Original corrupt file | Open app; inspect recovery/quarantine | No unsupported automatic repair deletes history; original preserved in quarantine; recovery state shown | Silent deletion or destructive repair | Quarantine inventory; UI state | M4 | NOT RUN |

### 4.2 Performance and resource use

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-011 | BS-NFR-PERF-001; BS-NFR-PERF-002; BS-NFR-PERF-003 | BS-NFR-PERF-001; BS-NFR-PERF-002; BS-NFR-PERF-003 | PERF | Performance | Reference environment; warm and cold starts | Installed build; 100,000 sessions | Measure cold UI start; warm UI start; agent ready | Cold ≤3 s; warm ≤1.5 s; agent ready ≤2 s | Any target missed | Metrics CSV; stopwatch trace | M5 | NOT RUN |
| BS-TEST-NFR-012 | BS-NFR-PERF-004; BS-NFR-PERF-005; BS-NFR-PERF-006 | BS-NFR-PERF-004; BS-NFR-PERF-005; BS-NFR-PERF-006 | PERF | Performance | Active session; healthy extension; controllable heartbeat | Foreground changes; session start; heartbeat stop | Measure detection, rule application, degradation | Detection ≤500 ms; rules applied ≤2 s; degradation ≤30 s | Any target missed | Timing capture; browser log | M5 | NOT RUN |
| BS-TEST-NFR-013 | BS-NFR-PERF-007; BS-NFR-PERF-008; BS-NFR-PERF-009 | BS-NFR-PERF-007; BS-NFR-PERF-008; BS-NFR-PERF-009 | PERF | Performance | 100,000 sessions; 1,000,000 events; normal navigation | Dashboard; task list; session summary; insights | Measure p95; navigate screens; record UI-thread stalls | Common DB p95 ≤100 ms; insights p95 ≤1 s; no UI-thread block >100 ms | Any target missed or frozen window | Metrics CSV; profiler trace | M5 | NOT RUN |
| BS-TEST-NFR-014 | BS-NFR-PERF-010; BS-NFR-PERF-011; BS-NFR-PERF-012; BS-NFR-PERF-013 | BS-NFR-PERF-010; BS-NFR-PERF-011; BS-NFR-PERF-012; BS-NFR-PERF-013 | PERF | Performance | Idle agent; active session; all primary screens | 10-minute idle; one blocked-app event per minute | Measure CPU and working set | Idle CPU ≤1%; active CPU ≤5%; agent working set ≤100 MB; UI working set ≤250 MB | Any target missed | Metrics CSV; task manager capture | M5 | NOT RUN |
| BS-TEST-NFR-015 | BS-NFR-PERF-014 | BS-NFR-PERF-014 | PERF | Performance/Integration | Active session; write monitor | Checkpoint interval; write volume | Run session; measure checkpoint cadence and amplification | Checkpoint ≤10 s; write amplification bounded | Missed interval or unbounded writes | Timing log; IO metrics | M5 | NOT RUN |
| BS-TEST-NFR-016 | BS-NFR-PERF-015 | BS-NFR-PERF-015 | PERF | Performance/UI | Install and uninstall both modes | Installer progress states | Observe installer; measure frozen periods | No operation appears frozen >5 s without status text | Frozen installer | Screen recording; timing log | M5 | NOT RUN |

### 4.3 Security

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-017 | BS-NFR-SEC-001; BS-NFR-SEC-002 | BS-NFR-SEC-001; BS-NFR-SEC-002 | SEC | Security | Agent pipe server; test clients | Other user; unapproved role; malformed/oversized/unknown-version frames | Attempt connection; send bad frames; inspect agent | User and role validated; malformed/oversized/unknown-version rejected without crash or mutation | Unauthorized access or crash | Security report; protocol log | M2 | NOT RUN |
| BS-TEST-NFR-018 | BS-NFR-SEC-003; BS-NFR-SEC-004 | BS-NFR-SEC-003; BS-NFR-SEC-004 | SEC | Security | Native host; extension manifest | Mismatched origin; permission list | Connect wrong origin; review permissions | Only allowlisted origin/fixed ID accepted; minimum permissions; no history/cookies/bookmarks/content | Origin bypass or excessive permission | Host log; manifest review | M2 | NOT RUN |
| BS-TEST-NFR-019 | BS-NFR-SEC-005; BS-NFR-SEC-006; BS-NFR-SEC-007 | BS-NFR-SEC-005; BS-NFR-SEC-006; BS-NFR-SEC-007 | SEC | Security | Extension source; service account; process monitor | Remote code probe; netstat; process actions | Review CSP/source; inspect service account and listener; inspect process actions | No remote code/eval; least privilege; no network listener; no arbitrary termination, injection, or undocumented autostart | Unsafe code or privilege overreach | Source review; netstat; process report | M2/M5 | NOT RUN |
| BS-TEST-NFR-020 | BS-NFR-SEC-008; BS-NFR-SEC-013 | BS-NFR-SEC-008; BS-NFR-SEC-013 | SEC | Security/Release | Signed build; update test harness | Installer; binaries; native host; future update | Verify signatures/hashes; test update signature and downgrade | All signed and hash-verified; future update verifies signature and cannot downgrade silently | Unsigned or downgrade accepted | Signature report; update test log | M5 | NOT RUN |
| BS-TEST-NFR-021 | BS-NFR-SEC-009 | BS-NFR-SEC-009 | SEC | Security | Seeded DB, backups, logs; second standard user | User-scoped ACLs | Inspect ACLs; attempt read/write as second user | Second standard user denied; no broad machine access | Cross-user access | ACL dump; access test | M2 | NOT RUN |
| BS-TEST-NFR-022 | BS-NFR-SEC-010 | BS-NFR-SEC-010 | SEC | Security | Modified privileged component | Tampered binary/service | Launch app; inspect trust state | Unsupported/modified component reported as degraded trust; no silent trust | Silent trust | Tamper test report; UI capture | M5 | NOT RUN |
| BS-TEST-NFR-023 | BS-NFR-SEC-011 | BS-NFR-SEC-011 | SEC | Security | Source, logs, client files | Secret patterns | Run secret scan; inspect release artifacts | No secrets, credentials, API keys, or tokens stored | Secret found | Secret scan report | M5 | NOT RUN |
| BS-TEST-NFR-024 | BS-NFR-SEC-012 | BS-NFR-SEC-012 | SEC | Security | Malicious archives; path traversal payloads | Oversized file; bad checksum; bad schema; `..\` path | Attempt restore/import | File type, size, checksum, schema, and path validated; no traversal or arbitrary overwrite | Traversal or overwrite | Security report; filesystem diff | M4 | NOT RUN |

### 4.4 Privacy and data protection

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-025 | BS-NFR-PRV-001; BS-NFR-PRV-003 | BS-NFR-PRV-001; BS-NFR-PRV-003 | PRV | Security/System | Installed app; network capture | Full core loop; study data; domains | Run offline; capture traffic | No account, cloud, telemetry, or automatic study-data transmission | Any required account or outbound data | Packet capture; offline report | M4 | NOT RUN |
| BS-TEST-NFR-026 | BS-NFR-PRV-002 | BS-NFR-PRV-002 | PRV | UI | First run | Data inventory; storage path; purpose | Read onboarding privacy content; compare with actual storage | Exactly what is stored, where, and why is disclosed before onboarding completes | Missing or inaccurate disclosure | Content review; onboarding capture | M3 | NOT RUN |
| BS-TEST-NFR-027 | BS-NFR-PRV-004; BS-NFR-PRV-005 | BS-NFR-PRV-004; BS-NFR-PRV-005 | PRV | Security | Seeded sensitive strings | URLs; titles; task notes; subject names; domains | Export diagnostics; inspect logs | User-initiated export; no notes, names, URLs, titles, or content in bundle or logs | Sensitive content present | Redaction report; log inspection | M2 | NOT RUN |
| BS-TEST-NFR-028 | BS-NFR-PRV-006; BS-NFR-PRV-007 | BS-NFR-PRV-006; BS-NFR-PRV-007 | PRV | System/Security | Seeded data; backups | Database; backups; logs; settings | Export and delete all; inspect backup upload | All local data exportable/deletable; backups local and never auto-uploaded | Hidden copy or upload | File inventory; packet capture | M4 | NOT RUN |
| BS-TEST-NFR-029 | BS-NFR-PRV-008 | BS-NFR-PRV-008 | PRV | Documentation | MVP build | Network feature probe | Search for opt-in network features; review scope | No opt-in network feature in MVP; future feature requires explicit consent and separate scope | Hidden network feature | Feature inventory; scope review | M5 | NOT RUN |
| BS-TEST-NFR-030 | BS-NFR-PRV-009; BS-NFR-PRV-010 | BS-NFR-PRV-009; BS-NFR-PRV-010 | PRV | Documentation/UI | Onboarding; About; distribution point; policy VM | Privacy summary; policy restriction | Open each link; inspect policy diagnostics | Concise privacy summary reachable; policy restrictions reported without extra device information | Missing summary or extra collection | Link checklist; diagnostics review | M5 | NOT RUN |

### 4.5 Accessibility

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-031 | BS-NFR-A11Y-001; BS-NFR-A11Y-002 | BS-NFR-A11Y-001; BS-NFR-A11Y-002 | A11Y | Accessibility | Windows 11; keyboard; Accessibility Insights | All core workflows | Navigate by keyboard; inspect UIA tree | Fully keyboard operable; visible focus; no traps; names, roles, states, and help text exposed | Trap or missing UIA data | Accessibility report; UIA export | M3 | NOT RUN |
| BS-TEST-NFR-032 | BS-NFR-A11Y-003 | BS-NFR-A11Y-003 | A11Y | Accessibility | Narrator enabled | Onboarding; task; session; summary; backup | Complete each workflow with Narrator | Narrator completes all listed workflows | Blocked or unannounced step | Narrator recording; script | M3 | NOT RUN |
| BS-TEST-NFR-033 | BS-NFR-A11Y-004; BS-NFR-A11Y-006 | BS-NFR-A11Y-004; BS-NFR-A11Y-006 | A11Y | Accessibility | High contrast mode | Status; focus; errors; notices; charts | Inspect each state; remove color | Visibility preserved; information never color-only | Invisible state or color-only meaning | High-contrast capture; accessibility review | M3/M5 | NOT RUN |
| BS-TEST-NFR-034 | BS-NFR-A11Y-005 | BS-NFR-A11Y-005 | A11Y | Accessibility/Compatibility | 100%, 150%, 200%; mixed-DPI dual monitor | All primary screens | Navigate at each scale; move between monitors | Usable; no clipped critical controls | Clipped or unreachable control | DPI matrix; screenshots after visual approval | M3/M5 | NOT RUN |
| BS-TEST-NFR-035 | BS-NFR-A11Y-007; BS-NFR-A11Y-008 | BS-NFR-A11Y-007; BS-NFR-A11Y-008 | A11Y | Accessibility | Reduced motion enabled; timer running | Motion; timer; state changes | Enable reduced motion; run session; observe announcements | Motion preference respected; timers and state changes announced without flooding | Ignored preference or announcement flood | Accessibility report; recording | M3 | NOT RUN |
| BS-TEST-NFR-036 | BS-NFR-A11Y-009; BS-NFR-A11Y-011; BS-NFR-A11Y-012 | BS-NFR-A11Y-009; BS-NFR-A11Y-011; BS-NFR-A11Y-012 | A11Y | Accessibility | Error states; blocking overlay | Error/recovery messages; overlay actions | Trigger errors; open overlay; use keyboard/pointer alternatives | Messages identify problem, feature, and next action; overlay keyboard accessible with exit; no essential sound, hover, or precision dependency | Unclear error or trapped overlay | Accessibility report; overlay recording | M3 | NOT RUN |
| BS-TEST-NFR-037 | BS-NFR-A11Y-010 | BS-NFR-A11Y-010 | A11Y | Accessibility | WCAG 2.2 AA audit; visual references absent | Desktop controls; contrast; typography | Run audit; separate visual items | Applicable criteria pass; visual contrast/typography/target size remain blocked until references exist | Unaudited applicable criterion | Audit report; blocked-item list | M5 | BLOCKED |

### 4.6 Compatibility and installation

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-038 | BS-NFR-COM-001 | BS-NFR-COM-001 | COM | Compatibility | Windows 11 supported builds; x64 | Install; core loop | Run supported-build matrix | MVP installs and runs on each supported x64 build | Failure on a supported build | OS matrix | M5 | NOT RUN |
| BS-TEST-NFR-039 | BS-NFR-COM-002; BS-NFR-COM-007 | BS-NFR-COM-002; BS-NFR-COM-007 | COM | Compatibility | Clean VMs: admin and standard user | Full and Limited installs | Install/uninstall both modes | Full requires admin; Limited requires standard user; Limited works without service | Wrong privilege or service dependency | Install matrix | M4 | NOT RUN |
| BS-TEST-NFR-040 | BS-NFR-COM-003 | BS-NFR-COM-003 | COM | Compatibility | DB created in each mode | Same path and schema | Open cross-mode; inspect path/schema | Same local data path and schema | Divergence | DB inspection | M4 | NOT RUN |
| BS-TEST-NFR-041 | BS-NFR-COM-004; BS-NFR-COM-005 | BS-NFR-COM-004; BS-NFR-COM-005 | COM | Compatibility | Chrome/Edge; other browser; policy VM | Browser matrix | Test supported, unsupported, and policy-blocked browsers | Chrome/Edge supported; others reported unsupported; policy block yields usable Limited mode and honest status | Unsupported claim or blocked product | Browser matrix; policy VM recording | M4 | NOT RUN |
| BS-TEST-NFR-042 | BS-NFR-COM-006 | BS-NFR-COM-006 | COM | Compatibility/Recovery | Full install | Reboot; logoff/logon; FUS; sleep/resume | Run lifecycle sequence | Full mode works after each transition | Failure after transition | Lifecycle matrix; logs | M4 | NOT RUN |
| BS-TEST-NFR-043 | BS-NFR-COM-008 | BS-NFR-COM-008 | COM | Compatibility/Recovery | Limited install; seeded data | Upgrade; canceled UAC | Upgrade; cancel; fail; retry | Data preserved; failure falls back to Limited without loss | Data loss or stuck mode | Upgrade report; before/after diff | M4 | NOT RUN |
| BS-TEST-NFR-044 | BS-NFR-COM-009 | BS-NFR-COM-009 | COM | Compatibility/System | Full and Limited installs | Service; task; pipe; files | Uninstall; inspect leftovers | No hidden service, autostart task, pipe, or privileged file | Leftover | Post-uninstall inspection | M4 | NOT RUN |
| BS-TEST-NFR-045 | BS-NFR-COM-010; BS-NFR-COM-011; BS-NFR-COM-012 | BS-NFR-COM-010; BS-NFR-COM-011; BS-NFR-COM-012 | COM | Compatibility/Integration | Build; Unicode data; timezone/DST data | Strings; subject/task names; dates | Review externalized strings; run Unicode; test date/time/timezone/DST/week boundaries | Strings externalized; Unicode supported; date/time/timezone/DST/week behavior matches domain rules | Hard-coded string or wrong date behavior | String review; Unicode test; timezone matrix | M5 | NOT RUN |

### 4.7 Maintainability and architecture

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-046 | BS-NFR-MNT-001; BS-NFR-MNT-002 | BS-NFR-MNT-001; BS-NFR-MNT-002 | MNT | Unit/Architecture | Codebase; dependency graph | Domain project; module references | Run dependency review and architecture tests | Domain independent of WPF, Windows APIs, EF Core, browser; explicit boundaries; no cycles | Forbidden dependency or cycle | Dependency report; architecture test | M2 | NOT RUN |
| BS-TEST-NFR-047 | BS-NFR-MNT-003; BS-NFR-MNT-008 | BS-NFR-MNT-003; BS-NFR-MNT-008 | MNT | Unit/CI | Domain test suite; CI | Business rules; warnings; static analysis | Run coverage review; build with warnings as errors; run analyzers | Rules covered before UI integration; build clean; static analysis passes | Uncovered rule or warning | Coverage report; CI log | M2/M5 | NOT RUN |
| BS-TEST-NFR-048 | BS-NFR-MNT-004; BS-NFR-MNT-005 | BS-NFR-MNT-004; BS-NFR-MNT-005 | MNT | Integration/Documentation | Migration suite; dependency list | Versioned migrations; new dependency | Run migration tests; review dependency justification and security | Migrations versioned, tested, reversible through backup; dependencies justified and reviewed | Untested migration or unreviewed dependency | Migration report; dependency review | M2/M5 | NOT RUN |
| BS-TEST-NFR-049 | BS-NFR-MNT-006; BS-NFR-OBS-006 | BS-NFR-MNT-006; BS-NFR-OBS-006 | MNT/OBS | Integration | Structured logs; UI-to-agent-to-bridge-to-service flow | Correlation IDs; rotated logs | Run one request; follow ID across components; expire a log | Correlation IDs present; request traceable end-to-end; rotated logs remain usable; no content | Broken correlation or missing trace | Correlation trace report; log excerpt | M2 | NOT RUN |
| BS-TEST-NFR-050 | BS-NFR-MNT-007; BS-NFR-MNT-009; BS-NFR-MNT-010 | BS-NFR-MNT-007; BS-NFR-MNT-009; BS-NFR-MNT-010 | MNT | Documentation/Architecture | Release build; ADR index; mode-specific code | Build metadata; ADRs; capability layers | Verify version metadata; review ADRs; inspect mode isolation | Reproducible version/build metadata; ADRs recorded; mode differences isolated in capability/platform layers | Missing metadata, ADR, or duplicated mode logic | Release checklist; ADR review; code review | M5 | NOT RUN |

### 4.8 Observability and supportability

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-051 | BS-NFR-OBS-001 | BS-NFR-OBS-001 | OBS | Integration/UI | All components; controllable failures | Service; agent; UI; bridge; extension; rules; database; backup | Open diagnostics in each health combination | Each component has a distinct health state | Missing or merged state | Diagnostics capture; health query | M3 | NOT RUN |
| BS-TEST-NFR-052 | BS-NFR-OBS-002; BS-NFR-OBS-003 | BS-NFR-OBS-002; BS-NFR-OBS-003 | OBS | Integration/Security | Rotating logs; seeded sensitive strings | Structured events; content probes | Inspect format, timestamps, tags, rotation; search sensitive content | Structured, timestamped, component-tagged, rotated; no prohibited personal content | Unstructured log or sensitive content | Log format check; redaction report | M2 | NOT RUN |
| BS-TEST-NFR-053 | BS-NFR-OBS-004; BS-NFR-OBS-005 | BS-NFR-OBS-004; BS-NFR-OBS-005 | OBS | UI/Support | Degraded install; error catalog | Health data; error states | Export diagnostics without developer help; trigger each error | Bundle exportable by user; each error maps to documented recovery action | Developer dependency or unmapped error | Bundle review; error-catalog check | M3 | NOT RUN |
| BS-TEST-NFR-054 | BS-NFR-OBS-006; BS-NFR-MNT-006 | BS-NFR-OBS-006; BS-NFR-MNT-006 | OBS/MNT | Integration | UI action that reaches agent, bridge, and service | Correlation ID; rotated logs | Follow one request across all logs; remove one component log | Trace reconstructable; missing component identifiable; no content exposed | Broken trace or content leak | Trace procedure; log excerpt | M2 | NOT RUN |
| BS-TEST-NFR-055 | BS-NFR-OBS-007 | BS-NFR-OBS-007 | OBS | System/Support | Offline environment | Support workflow | Attempt support workflow without remote service | No remote support dependency; local diagnostics suffice | Remote dependency | Offline support test | M5 | NOT RUN |

### 4.9 Release and compliance

| Test ID | Req | AC | Mod | Level | Preconditions | Data | Actions | Expected | Failure | Evidence | Milestone | Status |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| BS-TEST-NFR-056 | BS-NFR-RLS-001 | BS-NFR-RLS-001 | RLS | Documentation | Release candidate | Known limitations | Review document against enforcement, browsers, policy, admin, fullscreen, data access, uninstall | All listed topics covered accurately | Missing or misleading limitation | Document review; sign-off | M5 | NOT RUN |
| BS-TEST-NFR-057 | BS-NFR-RLS-002 | BS-NFR-RLS-002 | RLS | Documentation | Store submission package | Listings; permission justifications; privacy policy; support contact | Review package against store requirements | All required artifacts complete before submission | Missing artifact | Store checklist | M5 | NOT RUN |
| BS-TEST-NFR-058 | BS-NFR-RLS-003; BS-NFR-RLS-004 | BS-NFR-RLS-003; BS-NFR-RLS-004 | RLS | Release/Security | Signed build; product copy | Signatures; claims | Verify signatures; review copy for tamper/anti-cheat/guarantee claims | Signed; no prohibited claim | Unsigned or prohibited copy | Signature report; copy review | M5 | NOT RUN |
| BS-TEST-NFR-059 | BS-NFR-RLS-005; BS-NFR-RLS-006 | BS-NFR-RLS-005; BS-NFR-RLS-006 | RLS | System/Documentation | Full and Limited evidence sets; gap log | Independent test evidence; visual status | Review evidence separation; audit every screen | Independent evidence; visual approval accurate; blocked screens remain blocked | Shared evidence or false approval | Evidence index; gap-log audit | M5 | BLOCKED |
| BS-TEST-NFR-060 | BS-NFR-RLS-007 | BS-NFR-RLS-007 | RLS | Documentation | Release notes | Deferred features; known gaps | Review release notes against scope | Deferred features and known gaps disclosed | Missing disclosure | Release-note review | M5 | NOT RUN |

## 5. Requirement coverage index

The catalog above is authoritative. This index is generated from the requirement IDs in the catalog and is provided for quick lookup. A requirement may map to more than one test when the tests verify different behaviors.

### 5.1 Functional requirements

| Requirement | Priority | Test IDs |
|---|---|---|
| BS-XCT-001 | MUST | BS-TEST-FUNC-001; BS-TEST-FUNC-081; BS-TEST-FUNC-100 |
| BS-XCT-002 | MUST | BS-TEST-FUNC-002 |
| BS-XCT-003 | MUST | BS-TEST-FUNC-003 |
| BS-XCT-004 | MUST | BS-TEST-FUNC-004 |
| BS-XCT-005 | MUST | BS-TEST-FUNC-005 |
| BS-XCT-006 | MUST | BS-TEST-FUNC-006 |
| BS-XCT-007 | MUST | BS-TEST-FUNC-007 |
| BS-XCT-008 | MUST | BS-TEST-FUNC-008 |
| BS-XCT-009 | MUST | BS-TEST-FUNC-009; BS-TEST-FUNC-072 |
| BS-XCT-010 | MUST | BS-TEST-FUNC-010 |
| BS-ONB-001 | MUST | BS-TEST-FUNC-011 |
| BS-ONB-002 | MUST | BS-TEST-FUNC-012; BS-TEST-FUNC-087 |
| BS-ONB-003 | MUST | BS-TEST-FUNC-013 |
| BS-ONB-004 | MUST | BS-TEST-FUNC-014 |
| BS-ONB-005 | MUST | BS-TEST-FUNC-015 |
| BS-ONB-006 | MUST | BS-TEST-FUNC-016 |
| BS-ONB-007 | MUST | BS-TEST-FUNC-017 |
| BS-ONB-008 | MUST | BS-TEST-FUNC-018 |
| BS-ONB-009 | MUST | BS-TEST-FUNC-019 |
| BS-ONB-010 | MUST | BS-TEST-FUNC-020 |
| BS-ONB-011 | SHOULD | BS-TEST-FUNC-021 |
| BS-ONB-012 | MUST | BS-TEST-FUNC-022 |
| BS-DSH-001 | MUST | BS-TEST-FUNC-023 |
| BS-DSH-002 | MUST | BS-TEST-FUNC-024 |
| BS-DSH-003 | MUST | BS-TEST-FUNC-025 |
| BS-DSH-004 | MUST | BS-TEST-FUNC-026 |
| BS-DSH-005 | MUST | BS-TEST-FUNC-027 |
| BS-DSH-006 | MUST | BS-TEST-FUNC-028 |
| BS-DSH-007 | SHOULD | BS-TEST-FUNC-028 |
| BS-FOC-001 | MUST | BS-TEST-FUNC-029 |
| BS-FOC-002 | MUST | BS-TEST-FUNC-029 |
| BS-FOC-003 | MUST | BS-TEST-FUNC-030 |
| BS-FOC-004 | MUST | BS-TEST-FUNC-031 |
| BS-FOC-005 | MUST | BS-TEST-FUNC-031 |
| BS-FOC-006 | MUST | BS-TEST-FUNC-032 |
| BS-FOC-007 | MUST | BS-TEST-FUNC-031 |
| BS-FOC-008 | MUST | BS-TEST-FUNC-032 |
| BS-FOC-009 | MUST | BS-TEST-FUNC-030 |
| BS-FOC-010 | MUST | BS-TEST-FUNC-030; BS-TEST-FUNC-039 |
| BS-FOC-011 | MUST | BS-TEST-FUNC-033 |
| BS-FOC-012 | MUST | BS-TEST-FUNC-033 |
| BS-FOC-013 | MUST | BS-TEST-FUNC-029 |
| BS-FOC-014 | MUST | BS-TEST-FUNC-034; BS-TEST-FUNC-050 |
| BS-FOC-015 | MUST | BS-TEST-FUNC-032 |
| BS-FOC-016 | MUST | BS-TEST-FUNC-034 |
| BS-FOC-017 | MUST | BS-TEST-FUNC-035 |
| BS-FOC-018 | SHOULD | BS-TEST-FUNC-027 |
| BS-APP-001 | MUST | BS-TEST-FUNC-036 |
| BS-APP-002 | MUST | BS-TEST-FUNC-036 |
| BS-APP-003 | MUST | BS-TEST-FUNC-036 |
| BS-APP-004 | MUST | BS-TEST-FUNC-037 |
| BS-APP-005 | MUST | BS-TEST-FUNC-037 |
| BS-APP-006 | MUST | BS-TEST-FUNC-037 |
| BS-APP-007 | MUST | BS-TEST-FUNC-038 |
| BS-APP-008 | MUST | BS-TEST-FUNC-038 |
| BS-APP-009 | MUST | BS-TEST-FUNC-039 |
| BS-APP-010 | MUST | BS-TEST-FUNC-040 |
| BS-APP-011 | MUST | BS-TEST-FUNC-041 |
| BS-APP-012 | MUST | BS-TEST-FUNC-040 |
| BS-APP-013 | MUST | BS-TEST-FUNC-042 |
| BS-BRW-001 | MUST | BS-TEST-FUNC-043 |
| BS-BRW-002 | MUST | BS-TEST-FUNC-043 |
| BS-BRW-003 | MUST | BS-TEST-FUNC-043 |
| BS-BRW-004 | MUST | BS-TEST-FUNC-044 |
| BS-BRW-005 | MUST | BS-TEST-FUNC-045 |
| BS-BRW-006 | MUST | BS-TEST-FUNC-045 |
| BS-BRW-007 | MUST | BS-TEST-FUNC-045 |
| BS-BRW-008 | MUST | BS-TEST-FUNC-046 |
| BS-BRW-009 | MUST | BS-TEST-FUNC-046 |
| BS-BRW-010 | MUST | BS-TEST-FUNC-047 |
| BS-BRW-011 | MUST | BS-TEST-FUNC-047 |
| BS-BRW-012 | MUST | BS-TEST-FUNC-018; BS-TEST-FUNC-048; BS-TEST-FUNC-102 |
| BS-BRW-013 | MUST | BS-TEST-FUNC-049 |
| BS-BRW-014 | MUST | BS-TEST-FUNC-050 |
| BS-BRW-015 | MUST | BS-TEST-FUNC-051 |
| BS-BRW-016 | SHOULD | BS-TEST-FUNC-051 |
| BS-BRW-017 | MUST | BS-TEST-FUNC-046 |
| BS-BRW-018 | MUST | BS-TEST-FUNC-044; BS-TEST-FUNC-113 |
| BS-BRW-019 | MUST | BS-TEST-FUNC-051 |
| BS-STU-001 | MUST | BS-TEST-FUNC-052 |
| BS-STU-002 | MUST | BS-TEST-FUNC-053 |
| BS-STU-003 | MUST | BS-TEST-FUNC-053 |
| BS-STU-004 | MUST | BS-TEST-FUNC-053 |
| BS-STU-005 | MUST | BS-TEST-FUNC-054 |
| BS-STU-006 | MUST | BS-TEST-FUNC-055 |
| BS-STU-007 | MUST | BS-TEST-FUNC-055; BS-TEST-FUNC-063 |
| BS-STU-008 | MUST | BS-TEST-FUNC-052 |
| BS-STU-009 | SHOULD | BS-TEST-FUNC-056 |
| BS-STU-010 | MUST | BS-TEST-FUNC-057 |
| BS-STU-011 | MUST | BS-TEST-FUNC-054 |
| BS-STU-012 | SHOULD | BS-TEST-FUNC-056 |
| BS-GAM-001 | MUST | BS-TEST-FUNC-058 |
| BS-GAM-002 | MUST | BS-TEST-FUNC-058 |
| BS-GAM-003 | MUST | BS-TEST-FUNC-059 |
| BS-GAM-004 | MUST | BS-TEST-FUNC-059 |
| BS-GAM-005 | MUST | BS-TEST-FUNC-060 |
| BS-GAM-006 | MUST | BS-TEST-FUNC-060 |
| BS-GAM-007 | MUST | BS-TEST-FUNC-061 |
| BS-GAM-008 | MUST | BS-TEST-FUNC-062 |
| BS-GAM-009 | MUST | BS-TEST-FUNC-055; BS-TEST-FUNC-063 |
| BS-GAM-010 | MUST | BS-TEST-FUNC-064; BS-TEST-FUNC-084 |
| BS-GAM-011 | MUST | BS-TEST-FUNC-062 |
| BS-GAM-012 | MUST | BS-TEST-FUNC-065; BS-TEST-FUNC-080 |
| BS-GAM-013 | MUST | BS-TEST-FUNC-061 |
| BS-GAM-014 | MUST | BS-TEST-FUNC-066 |
| BS-GAM-015 | MUST | BS-TEST-FUNC-066 |
| BS-GAM-016 | MUST | BS-TEST-FUNC-066 |
| BS-GAM-017 | SHOULD | BS-TEST-FUNC-066 |
| BS-GAM-018 | MUST | BS-TEST-FUNC-115 |
| BS-INS-001 | MUST | BS-TEST-FUNC-067 |
| BS-INS-002 | MUST | BS-TEST-FUNC-067 |
| BS-INS-003 | MUST | BS-TEST-FUNC-068 |
| BS-INS-004 | MUST | BS-TEST-FUNC-068 |
| BS-INS-005 | MUST | BS-TEST-FUNC-069 |
| BS-INS-006 | MUST | BS-TEST-FUNC-069 |
| BS-INS-007 | MUST | BS-TEST-FUNC-070 |
| BS-INS-008 | MUST | BS-TEST-FUNC-070 |
| BS-INS-009 | MUST | BS-TEST-FUNC-067 |
| BS-INS-010 | SHOULD | BS-TEST-FUNC-071 |
| BS-DAT-001 | MUST | BS-TEST-FUNC-072; BS-TEST-FUNC-103 |
| BS-DAT-002 | MUST | BS-TEST-FUNC-009; BS-TEST-FUNC-072 |
| BS-DAT-003 | MUST | BS-TEST-FUNC-073 |
| BS-DAT-004 | MUST | BS-TEST-FUNC-073 |
| BS-DAT-005 | MUST | BS-TEST-FUNC-074 |
| BS-DAT-006 | MUST | BS-TEST-FUNC-005; BS-TEST-FUNC-074 |
| BS-DAT-007 | MUST | BS-TEST-FUNC-074 |
| BS-DAT-008 | MUST | BS-TEST-FUNC-075 |
| BS-DAT-009 | MUST | BS-TEST-FUNC-076 |
| BS-DAT-010 | MUST | BS-TEST-FUNC-005; BS-TEST-FUNC-077 |
| BS-DAT-011 | MUST | BS-TEST-FUNC-005; BS-TEST-FUNC-077 |
| BS-DAT-012 | MUST | BS-TEST-FUNC-078 |
| BS-DAT-013 | MUST | BS-TEST-FUNC-078 |
| BS-DAT-014 | MUST | BS-TEST-FUNC-078 |
| BS-DAT-015 | MUST | BS-TEST-FUNC-075 |
| BS-DAT-016 | MUST | BS-TEST-FUNC-079; BS-TEST-FUNC-096 |
| BS-DAT-017 | MUST | BS-TEST-FUNC-065; BS-TEST-FUNC-080 |
| BS-DAT-018 | MUST | BS-TEST-FUNC-001; BS-TEST-FUNC-081; BS-TEST-FUNC-100 |
| BS-NOT-001 | MUST | BS-TEST-FUNC-082 |
| BS-NOT-002 | MUST | BS-TEST-FUNC-082 |
| BS-NOT-003 | MUST | BS-TEST-FUNC-083 |
| BS-NOT-004 | MUST | BS-TEST-FUNC-064; BS-TEST-FUNC-084 |
| BS-NOT-005 | MUST | BS-TEST-FUNC-083 |
| BS-NOT-006 | SHOULD | BS-TEST-FUNC-085; BS-TEST-FUNC-106 |
| BS-NOT-007 | MUST | BS-TEST-FUNC-086 |
| BS-NOT-008 | MUST | BS-TEST-FUNC-086 |
| BS-NOT-009 | MUST | BS-TEST-FUNC-086 |
| BS-NOT-010 | MUST | BS-TEST-FUNC-086 |
| BS-SYS-001 | MUST | BS-TEST-FUNC-012; BS-TEST-FUNC-087 |
| BS-SYS-002 | MUST | BS-TEST-FUNC-015; BS-TEST-FUNC-088 |
| BS-SYS-003 | MUST | BS-TEST-FUNC-016; BS-TEST-FUNC-089 |
| BS-SYS-004 | MUST | BS-TEST-FUNC-088 |
| BS-SYS-005 | MUST | BS-TEST-FUNC-090 |
| BS-SYS-006 | MUST | BS-TEST-FUNC-091 |
| BS-SYS-007 | MUST | BS-TEST-FUNC-091 |
| BS-SYS-008 | MUST | BS-TEST-FUNC-091 |
| BS-SYS-009 | MUST | BS-TEST-FUNC-088 |
| BS-SYS-010 | MUST | BS-TEST-FUNC-089; BS-TEST-FUNC-107 |
| BS-SYS-011 | MUST | BS-TEST-FUNC-092 |
| BS-SYS-012 | MUST | BS-TEST-FUNC-093 |
| BS-SYS-013 | MUST | BS-TEST-FUNC-092 |
| BS-SYS-014 | MUST | BS-TEST-FUNC-008; BS-TEST-FUNC-092 |
| BS-SYS-015 | MUST | BS-TEST-FUNC-043; BS-TEST-FUNC-093 |
| BS-SYS-016 | MUST | BS-TEST-FUNC-094 |
| BS-SYS-017 | MUST | BS-TEST-FUNC-094 |
| BS-SYS-018 | MUST | BS-TEST-FUNC-094 |
| BS-SYS-019 | MUST | BS-TEST-FUNC-033; BS-TEST-FUNC-095 |
| BS-SYS-020 | MUST | BS-TEST-FUNC-079; BS-TEST-FUNC-096 |
| BS-SYS-021 | MUST | BS-TEST-FUNC-095 |
| BS-SYS-022 | MUST | BS-TEST-FUNC-026; BS-TEST-FUNC-097 |
| BS-SYS-023 | MUST | BS-TEST-FUNC-097 |
| BS-SYS-024 | MUST | BS-TEST-FUNC-098 |
| BS-SYS-025 | MUST | BS-TEST-FUNC-098 |
| BS-SYS-026 | MUST | BS-TEST-FUNC-099 |
| BS-SYS-027 | MUST | BS-TEST-FUNC-001; BS-TEST-FUNC-081; BS-TEST-FUNC-100 |
| BS-SYS-028 | MUST | BS-TEST-FUNC-003; BS-TEST-FUNC-099 |
| BS-SYS-029 | MUST | BS-TEST-FUNC-101 |
| BS-SYS-030 | MUST | BS-TEST-FUNC-016; BS-TEST-FUNC-089 |
| BS-SYS-031 | MUST | BS-TEST-FUNC-018; BS-TEST-FUNC-048; BS-TEST-FUNC-102 |
| BS-SYS-032 | MUST | BS-TEST-FUNC-103 |
| BS-SYS-033 | MUST | BS-TEST-FUNC-013; BS-TEST-FUNC-101 |
| BS-SYS-034 | MUST | BS-TEST-FUNC-004; BS-TEST-FUNC-097 |
| BS-SYS-035 | MUST | BS-TEST-FUNC-090 |
| BS-SET-001 | MUST | BS-TEST-FUNC-104 |
| BS-SET-002 | MUST | BS-TEST-FUNC-036; BS-TEST-FUNC-105 |
| BS-SET-003 | MUST | BS-TEST-FUNC-105 |
| BS-SET-004 | MUST | BS-TEST-FUNC-085; BS-TEST-FUNC-106 |
| BS-SET-005 | MUST | BS-TEST-FUNC-107 |
| BS-SET-006 | MUST | BS-TEST-FUNC-108 |
| BS-SET-007 | MUST | BS-TEST-FUNC-109 |
| BS-SET-008 | MUST | BS-TEST-FUNC-022; BS-TEST-FUNC-110 |
| BS-SET-009 | MUST | BS-TEST-FUNC-110 |
| BS-SET-010 | MUST | BS-TEST-FUNC-104 |
| BS-SET-011 | MUST | BS-TEST-FUNC-111 |
| BS-SET-012 | MUST | BS-TEST-FUNC-111 |
| BS-SET-013 | SHOULD | BS-TEST-FUNC-112 |
| BS-SET-014 | MUST | BS-TEST-FUNC-112 |
| BS-SET-015 | MUST | BS-TEST-FUNC-044; BS-TEST-FUNC-113 |
| BS-SET-016 | MUST | BS-TEST-FUNC-114 |

### 5.2 Non-functional requirements

| Requirement | Priority | Test IDs |
|---|---|---|
| BS-NFR-REL-001 | MUST | BS-TEST-NFR-001 |
| BS-NFR-REL-002 | MUST | BS-TEST-NFR-002 |
| BS-NFR-REL-003 | MUST | BS-TEST-NFR-003 |
| BS-NFR-REL-004 | MUST | BS-TEST-NFR-004 |
| BS-NFR-REL-005 | MUST | BS-TEST-NFR-005 |
| BS-NFR-REL-006 | MUST | BS-TEST-NFR-006 |
| BS-NFR-REL-007 | MUST | BS-TEST-NFR-007 |
| BS-NFR-REL-008 | MUST | BS-TEST-NFR-008 |
| BS-NFR-REL-009 | MUST | BS-TEST-NFR-009 |
| BS-NFR-REL-010 | MUST | BS-TEST-NFR-010 |
| BS-NFR-PERF-001 | MUST | BS-TEST-NFR-011 |
| BS-NFR-PERF-002 | MUST | BS-TEST-NFR-011 |
| BS-NFR-PERF-003 | MUST | BS-TEST-NFR-011 |
| BS-NFR-PERF-004 | MUST | BS-TEST-NFR-012 |
| BS-NFR-PERF-005 | MUST | BS-TEST-NFR-012 |
| BS-NFR-PERF-006 | MUST | BS-TEST-NFR-012 |
| BS-NFR-PERF-007 | MUST | BS-TEST-NFR-013 |
| BS-NFR-PERF-008 | MUST | BS-TEST-NFR-013 |
| BS-NFR-PERF-009 | SHOULD | BS-TEST-NFR-013 |
| BS-NFR-PERF-010 | MUST | BS-TEST-NFR-014 |
| BS-NFR-PERF-011 | MUST | BS-TEST-NFR-014 |
| BS-NFR-PERF-012 | SHOULD | BS-TEST-NFR-014 |
| BS-NFR-PERF-013 | SHOULD | BS-TEST-NFR-014 |
| BS-NFR-PERF-014 | MUST | BS-TEST-NFR-015 |
| BS-NFR-PERF-015 | MUST | BS-TEST-NFR-016 |
| BS-NFR-SEC-001 | MUST | BS-TEST-NFR-017 |
| BS-NFR-SEC-002 | MUST | BS-TEST-NFR-017 |
| BS-NFR-SEC-003 | MUST | BS-TEST-NFR-018 |
| BS-NFR-SEC-004 | MUST | BS-TEST-NFR-018 |
| BS-NFR-SEC-005 | MUST | BS-TEST-NFR-019 |
| BS-NFR-SEC-006 | MUST | BS-TEST-NFR-019 |
| BS-NFR-SEC-007 | MUST | BS-TEST-NFR-019 |
| BS-NFR-SEC-008 | MUST | BS-TEST-NFR-020 |
| BS-NFR-SEC-009 | MUST | BS-TEST-NFR-021 |
| BS-NFR-SEC-010 | SHOULD | BS-TEST-NFR-022 |
| BS-NFR-SEC-011 | MUST | BS-TEST-NFR-023 |
| BS-NFR-SEC-012 | MUST | BS-TEST-NFR-024 |
| BS-NFR-SEC-013 | MUST | BS-TEST-NFR-020 |
| BS-NFR-PRV-001 | MUST | BS-TEST-FUNC-002; BS-TEST-NFR-025 |
| BS-NFR-PRV-002 | MUST | BS-TEST-NFR-026 |
| BS-NFR-PRV-003 | MUST | BS-TEST-FUNC-002; BS-TEST-NFR-025 |
| BS-NFR-PRV-004 | MUST | BS-TEST-NFR-027 |
| BS-NFR-PRV-005 | MUST | BS-TEST-NFR-027 |
| BS-NFR-PRV-006 | MUST | BS-TEST-NFR-028 |
| BS-NFR-PRV-007 | MUST | BS-TEST-NFR-028 |
| BS-NFR-PRV-008 | MUST | BS-TEST-NFR-029 |
| BS-NFR-PRV-009 | SHOULD | BS-TEST-NFR-030 |
| BS-NFR-PRV-010 | MUST | BS-TEST-NFR-030 |
| BS-NFR-A11Y-001 | MUST | BS-TEST-FUNC-006; BS-TEST-NFR-031 |
| BS-NFR-A11Y-002 | MUST | BS-TEST-NFR-031 |
| BS-NFR-A11Y-003 | MUST | BS-TEST-FUNC-006; BS-TEST-NFR-032 |
| BS-NFR-A11Y-004 | MUST | BS-TEST-FUNC-109; BS-TEST-NFR-033 |
| BS-NFR-A11Y-005 | MUST | BS-TEST-FUNC-109; BS-TEST-NFR-034 |
| BS-NFR-A11Y-006 | MUST | BS-TEST-NFR-033 |
| BS-NFR-A11Y-007 | MUST | BS-TEST-FUNC-109; BS-TEST-NFR-035 |
| BS-NFR-A11Y-008 | MUST | BS-TEST-NFR-035 |
| BS-NFR-A11Y-009 | MUST | BS-TEST-NFR-036 |
| BS-NFR-A11Y-010 | SHOULD | BS-TEST-NFR-037 |
| BS-NFR-A11Y-011 | MUST | BS-TEST-NFR-036 |
| BS-NFR-A11Y-012 | MUST | BS-TEST-NFR-036 |
| BS-NFR-COM-001 | MUST | BS-TEST-NFR-038 |
| BS-NFR-COM-002 | MUST | BS-TEST-NFR-039 |
| BS-NFR-COM-003 | MUST | BS-TEST-NFR-040 |
| BS-NFR-COM-004 | MUST | BS-TEST-NFR-041 |
| BS-NFR-COM-005 | MUST | BS-TEST-NFR-041 |
| BS-NFR-COM-006 | MUST | BS-TEST-NFR-042 |
| BS-NFR-COM-007 | MUST | BS-TEST-NFR-039 |
| BS-NFR-COM-008 | MUST | BS-TEST-NFR-043 |
| BS-NFR-COM-009 | MUST | BS-TEST-NFR-044 |
| BS-NFR-COM-010 | SHOULD | BS-TEST-NFR-045 |
| BS-NFR-COM-011 | MUST | BS-TEST-NFR-045 |
| BS-NFR-COM-012 | MUST | BS-TEST-NFR-045 |
| BS-NFR-MNT-001 | MUST | BS-TEST-NFR-046 |
| BS-NFR-MNT-002 | MUST | BS-TEST-NFR-046 |
| BS-NFR-MNT-003 | MUST | BS-TEST-NFR-047 |
| BS-NFR-MNT-004 | MUST | BS-TEST-NFR-048 |
| BS-NFR-MNT-005 | MUST | BS-TEST-NFR-048 |
| BS-NFR-MNT-006 | MUST | BS-TEST-NFR-049; BS-TEST-NFR-054 |
| BS-NFR-MNT-007 | MUST | BS-TEST-NFR-050 |
| BS-NFR-MNT-008 | SHOULD | BS-TEST-NFR-047 |
| BS-NFR-MNT-009 | MUST | BS-TEST-NFR-050 |
| BS-NFR-MNT-010 | MUST | BS-TEST-NFR-050 |
| BS-NFR-OBS-001 | MUST | BS-TEST-NFR-051 |
| BS-NFR-OBS-002 | MUST | BS-TEST-NFR-052 |
| BS-NFR-OBS-003 | MUST | BS-TEST-NFR-052 |
| BS-NFR-OBS-004 | MUST | BS-TEST-NFR-053 |
| BS-NFR-OBS-005 | MUST | BS-TEST-FUNC-007; BS-TEST-NFR-053 |
| BS-NFR-OBS-006 | SHOULD | BS-TEST-NFR-049; BS-TEST-NFR-054 |
| BS-NFR-OBS-007 | MUST | BS-TEST-NFR-055 |
| BS-NFR-RLS-001 | MUST | BS-TEST-NFR-056 |
| BS-NFR-RLS-002 | MUST | BS-TEST-NFR-057 |
| BS-NFR-RLS-003 | MUST | BS-TEST-NFR-058 |
| BS-NFR-RLS-004 | MUST | BS-TEST-NFR-058 |
| BS-NFR-RLS-005 | MUST | BS-TEST-NFR-059 |
| BS-NFR-RLS-006 | MUST | BS-TEST-FUNC-010; BS-TEST-NFR-059 |
| BS-NFR-RLS-007 | MUST | BS-TEST-NFR-060 |

## 6. Coverage summary

| Area | Requirements | Covered | Tests |
|---|---|---|---|
| Functional | 198 (188 MUST, 10 SHOULD) | 198 | 115 named functional cases |
| NFR reliability | 10 | 10 | 10 |
| NFR performance | 15 | 15 | 6 |
| NFR security | 13 | 13 | 8 |
| NFR privacy | 10 | 10 | 6 |
| NFR accessibility | 12 | 12 | 7 |
| NFR compatibility | 12 | 12 | 8 |
| NFR maintainability | 10 | 10 | 5 |
| NFR observability | 7 | 7 | 5 |
| NFR release | 7 | 7 | 5 |
| Total NFR | 96 (87 MUST, 9 SHOULD) | 96 | 60 named NFR cases |

**Execution status:** 0 passed, 0 failed, 172 not run, 3 blocked. No case has evidence. The counts above are definition coverage, not verification. The matrix becomes evidence of quality only when each case has a build identifier, environment, actual result, and pass/fail status.

**Coverage rule:** A requirement is covered only by a test whose actions and expected result genuinely verify it. Grouped tests in this matrix list every requirement they verify and include a specific assertion for each. No test is counted as coverage merely because it mentions a module or suite.

**Change control:** Adding or changing a requirement requires adding or updating its test mapping before the requirement can be marked complete. Removing a test requires reassigning every mapped requirement. A `SHOULD` requirement without a passing test needs a recorded user-approved exception.
