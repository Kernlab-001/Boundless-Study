# Phase 3 — Architecture and Technical Validation Record

**Product:** Boundless Study  
**Phase:** 3 — Architecture and Technical Validation  
**Status:** Executed; completion conditional on user acceptance of deferred Full-mode and external-distribution risks  
**Date:** 2026-11-12  
**Baseline:** Approved/frozen Phase 2 archive (`docs/01-product` through `docs/06-user`)  
**Related:** `../04-architecture/system-architecture.md`, `../04-architecture/adr/`, `../05-quality/test-strategy.md`

> **Security note (2026-11-15):** local machine/user identifiers and the full temporary path in this historical record were redacted during Milestone 3 security closure. No measured result or status changed.

## 0. Scope and evidence rules

This record covers the Phase 3 technical questions:

- Q-002 — installer technology.
- Q-003 — service-to-agent restart and recovery.
- Q-004 — browser publishing, extension ID, and signing.
- Q-006 — supported Windows and browser versions.

It also records bounded validation of the architecture boundaries: service/agent supervision, SQLite and IPC, browser integration, installer modes, and compatibility.

No product application, solution, scaffolding, installer product, service, agent, extension, database implementation, or UI asset was created. Isolated proof-of-concept code lived only under:

`C:\Users\<user>\AppData\Local\Temp\opencode\bs-phase3\`

Status values used below:

| Status | Meaning |
|---|---|
| `VERIFIED` | Demonstrated by an executed experiment in this environment. |
| `DOCUMENTED` | Supported by authoritative vendor documentation or the frozen baseline. |
| `RECOMMENDED` | Engineering recommendation without complete experimental proof. |
| `BLOCKED` | Cannot be validated with the available environment. |
| `REJECTED` | Failed validation or unacceptable risk. |

A recommendation is never recorded as a passed test.

### 0.1 Environment

| Item | Value |
|---|---|
| OS | Windows 11 26H2, build `26300.9974`, x64 |
| Reported OS version | `10.0.26300.0`; registry `ProductName` reports “Windows 10 Home Single Language” |
| Elevation | Not elevated; Administrators group is deny-only; HKLM writes fail |
| Session | Interactive session 2; services session 0 is separate |
| .NET | Runtime `10.0.14` (NETCore and WindowsDesktop); no .NET SDK; no MSBuild |
| Compilers | .NET Framework 4.8 `csc.exe` only |
| Browsers | Chrome `157.0.8103.51`; Edge `157.0.4341.46` |
| Validation tools | Node `26.12.1`, Python `3.12.10`, SQLite `3.49.1`, 7-Zip `26.01` |
| Installer tool | Inno Setup `6.0.5` compiler extracted into the temp area; not installed |
| Missing tooling | WiX, NSIS, signtool, Windows SDK, Hyper-V tooling |
| .NET SDK (added 2026-11-13) | `10.0.404` installed **per-user** into the temp area via `dotnet-install.ps1`; not machine-wide, not on PATH by default. Unlocked EF Core validation. |
| UAC note | During the closure round, two installer runs requested elevation through the normal UAC prompt (consent granted interactively). `ConsentPromptBehaviorAdmin=5`, `EnableLUA=1`. |
| VM capability | No clean Windows VM available without elevation/tooling |

### 0.2 Baseline contradiction check

No contradiction was found in the approved requirements, acceptance criteria, economy rules, database definitions, or architecture decisions during these spikes. Two environment/support gaps are recorded without changing the baseline:

1. The local OS reports Windows 11 26H2, while the .NET 10 supported-OS page last updated 2026-09-28 lists 26H1 but not 26H2. This is a vendor-support gap, not a product-document contradiction.
2. The local Chrome branded build ignores the old `--load-extension` command-line switch. This is a validation-harness constraint, not a product requirement change.

## 1. Technical Validation Plan

### 1.1 Principles

- Prefer one reproducible experiment over speculative documentation.
- Keep proof-of-concept code outside the product repository.
- Test the riskiest assumption in each boundary first.
- Record failures and unknowns, not only successes.
- Do not claim a Windows service, installer, store, or signing result that was not executed.
- Do not alter the frozen baseline without user approval.

### 1.2 Spikes

| Spike | Technical question | Smallest useful experiment | Environment | Planned output |
|---|---|---|---|---|
| P3-S1 | Which Windows and browser versions are actually supported by the approved stack? | Compare official .NET 10, Edge, and Chrome support statements with the local build and runtime. | Local + vendor documentation | Compatibility matrix; Q-006 disposition |
| P3-S2 | Does Chromium MV3 load with a fixed extension ID? | Load an isolated MV3 extension with a manifest `key`; derive and compare the extension ID in Chrome and Edge. | Chrome 157; Edge 157; headless | Extension ID and MV3 load evidence |
| P3-S3 | Do dynamic and session DNR rules work and survive restart? | Add a dynamic block rule and a session rule; call `testMatchOutcome`; restart the browser; read both rule sets. | Edge 157; Chrome 157 | DNR evidence and persistence limitation |
| P3-S4 | Does HKCU Native Messaging registration launch a host and exchange framed messages? | Register a compiled host in HKCU for Chrome and Edge; connect with `chrome.runtime.connectNative`; send ping; receive pong. | Chrome 157; Edge 157 | Native Messaging evidence |
| P3-S5 | Does SQLite WAL support concurrent reads, snapshots, backup, and crash recovery? | Python `sqlite3` experiment: open write + reader, snapshot transaction, busy timeout, online backup, forced process exit before/after commit. | Python 3.12; SQLite 3.49.1 | SQLite evidence |
| P3-S6 | Does the ledger design prevent duplicate rewards and partial commits? | Unique idempotency key, duplicate insert, simulated failure with rollback, concurrent duplicate writers. | Python 3.12; SQLite 3.49.1 | Ledger evidence |
| P3-S7 | Does the named-pipe contract handle framing, concurrency, timeout/cancel, malformed input, and reconnect? | Node server/client: 10 clients × 20 correlated requests; slow request timeout; cancel; oversized frame; server restart. | Node 26.12.1 | IPC evidence |
| P3-S8 | Can a message-mode pipe be created with a user-scoped ACL? | .NET Framework `NamedPipeServerStream` with a protected DACL; write two frames back-to-back; read boundaries. | .NET Framework 4.8 | Message-mode and ACL evidence |
| P3-S9 | Can an installer support Limited mode without elevation and preserve data on upgrade/uninstall? | Build a real Inno Setup installer; install, upgrade, and uninstall silently as the current user; inspect files, HKCU keys, and data. | Inno Setup 6.0.5; non-elevated | Installer evidence |
| P3-S10 | What does Windows session isolation require for service-to-agent launch? | Enumerate WTS sessions; call `WTSQueryUserToken`; test a named mutex; inspect privileges. | Non-elevated Windows 11 | Service/session constraints |
| P3-S11 | What remains for store distribution and signing? | Review store/signing requirements; confirm what can be tested locally. | Documentation + local registry | Q-004 dependency list |
| P3-S12 | Does the LocalSystem-service + per-user-task supervision design work? | User-mode supervisor PoC plays the service role: register per-user logon-trigger task, launch agent via Task Scheduler, control-pipe heartbeat, agent kill, supervisor kill, crash-loop with rate limiting. | Non-elevated Windows 11; real Task Scheduler; real named pipes | Supervision-loop evidence and remaining service-only gaps |
| P3-S13 | Does the Full-mode installer lifecycle work? | Compile a `PrivilegesRequired=admin` Inno script with HKLM registration and `sc.exe` service commands; execute the elevated install/uninstall lifecycle; record non-elevated denial boundaries. | Inno Setup 6.0.5; UAC consent | Full-mode installer evidence and registry-redirection finding |
| P3-S14 | Does EF Core 10 + Microsoft.Data.Sqlite meet the data requirements? | Per-user .NET 10 SDK; isolated net10.0 console spike over a representative model subset: migrations, handshake, tamper detection, transactions, concurrency, backup/restore, WAL, interrupted migration, schema upgrade. | .NET 10 SDK 10.0.404 (per-user); EF Core 10; SQLite | EF Core evidence replacing the previous `BLOCKED` |
| P3-S15 | What do the browser stores actually require? | Official Chrome Web Store and Edge Add-ons publishing documentation; manifest `key` ID-stability workflow. | Vendor documentation | Q-004 documented requirements and owner list |
| P3-S16 | What can foreground/fullscreen/multi-monitor detection actually observe? | Win32 PoC: enumerate monitors, poll foreground window, measure detection latency. | Non-elevated Windows 11 | Guard-boundary evidence and timing numbers |

### 1.3 Not executable in this environment

| Area | Reason | Disposition |
|---|---|---|
| Full-mode service install/start/stop | Requires elevation and a service-control environment | `BLOCKED` (elevated runbook §3.13) |
| Logon/logoff, fast user switching, sleep/resume, reboot supervision | Requires an elevated service and interactive session control | `BLOCKED` (elevated runbook §3.13) |
| EF Core migrations and `Microsoft.Data.Sqlite` behavior | ~~No .NET SDK; no NuGet build~~ Solved 2026-11-13: per-user .NET 10 SDK 10.0.404 installed into the temp area | Executed — P3-S14 `VERIFIED` (§3.11) |
| Clean-VM install/uninstall | No VM tooling or second clean machine | `BLOCKED` (elevated runbook §3.13) |
| Application-guard product-level enforcement (overlay, policy application, anti-tamper) | Product code not authorized/built | `BLOCKED` for Phase 3; PoC-level detection executed — P3-S16 (§3.13) |
| Chrome Web Store / Edge Add-ons accounts and review | External accounts, identity, and payment | `BLOCKED` (external) |
| Authenticode code signing | No certificate, no signtool | `BLOCKED` (external) |
| Browser policy push, heartbeat, focus-session synchronization | Requires the agent/bridge integration that Phase 3 did not build | `BLOCKED`; `DOCUMENTED` design only |

## 2. Spike Register

| Spike | Question | Requirements / ADRs | Result | Disposition |
|---|---|---|---|---|
| P3-S1 | Supported Windows and browser versions | BS-NFR-COM-001; BS-NFR-COM-004; Q-006 | .NET 10 lists Windows 11 26H1, 25H2, 24H2, and 23H2 (Enterprise/Education); Chrome supports Windows 10+ Intel and Windows 11+ ARM; Edge supports Windows 11 and Windows 10 SAC 1709+. Local 26H2 runs .NET 10 and both browsers, but 26H2 is not yet on the .NET 10 list. | `DOCUMENTED`; Q-006 resolved with conditions |
| P3-S2 | MV3 load and fixed extension ID | ADR-0004; BS-NFR-SEC-003 | Edge loaded the MV3 service worker and test page with ID `jjfgoeghdckpppcfjinhcccffmkmkibn`; Chrome loaded the same extension and ID through `Extensions.loadUnpacked`. | `VERIFIED` |
| P3-S3 | DNR dynamic/session rules and restart behavior | ADR-0004 | Dynamic rule 1001 blocked `example.com`; `testMatchOutcome` matched it; session rule 2001 existed. After an Edge restart, dynamic rule 1001 persisted, session rule 2001 was gone, and Native Messaging reconnected. Chrome set/matched rules but its unpacked harness reloads the extension on each launch, so Chrome restart persistence is not proven locally. | `VERIFIED` for rule mechanics; `DOCUMENTED` for Chrome persistence; restart persistence `VERIFIED` in Edge |
| P3-S4 | Native Messaging via HKCU | ADR-0004; BS-NFR-SEC-003 | Both browsers launched the compiled host and returned `{"type":"pong","echo":42,...}`. Host manifest allowlist used the fixed extension ID. | `VERIFIED` for registration, launch, and ping/pong; policy/heartbeat not tested |
| P3-S5 | SQLite WAL, snapshot, backup, crash recovery | ADR-0005; BS-NFR-REL-003/009/010 | WAL enabled; reader saw 1 committed row during an open write and 101 after commit; snapshot stayed 101 inside the transaction and saw 126 after commit; zero-timeout writer failed with “database is locked”; 5-second waiter succeeded after 0.657 s; online backup integrity `ok` with 240 rows; crash before commit rolled back all 500 rows; crash after commit retained 500 rows plus marker; `integrity_check` remained `ok`; WAL truncated to 0 bytes. | `VERIFIED` for SQLite engine behavior; EF Core integration verified separately in P3-S14 (§3.11) |
| P3-S6 | Ledger idempotency and atomicity | ADR-0006; BS-NFR-REL-003 | Duplicate key raised `UNIQUE constraint failed`; ledger stayed at 1 row and 25 XP. Simulated failure rolled back the reward and source update. Two concurrent writers with the same key produced exactly one commit and one duplicate error. | `VERIFIED` for SQLite mechanics; domain-layer transaction code not built |
| P3-S7 | Named-pipe framing, concurrency, timeout, reconnect | BS-NFR-SEC; system architecture §7.2 | 10 clients × 20 requests = 200 correlated responses in 17 ms; timeout fired; connection survived; cancel returned `cancelled`; 2 MB frame closed the connection; server restart and reconnect worked; stopped server refused connections. | `VERIFIED` |
| P3-S8 | Message-mode pipe and user-scoped ACL | System architecture §7.2; BS-NFR-SEC | `TransmissionMode=Message`; protected DACL `D:P(A;;0x1f019f;;;SY)(A;;0x1f019f;;;S-1-5-21-...-1001)`; two back-to-back frames preserved boundaries; response `ok`. | `VERIFIED` for message mode and DACL construction; cross-user denial `DOCUMENTED`, not executed |
| P3-S9 | Limited-mode installer, upgrade, uninstall | ADR-0007; ADR-0010; Q-002 | Inno Setup 6.0.5 produced a single EXE. Limited install exited 0 to `%LocalAppData%\Programs\BoundlessStudySpike`; HKCU Chrome/Edge host keys and Run value existed; no Program Files directory and no service were created. Upgrade to 1.1.0 preserved seeded user data and kept one uninstall entry. Uninstall removed the app and HKCU keys and preserved `%LocalAppData%\BoundlessStudySpike\data`. | `VERIFIED` for Limited mode; Full-mode lifecycle executed separately in P3-S13 (§3.10); Q-002 `RECOMMENDED`, not fully resolved |
| P3-S10 | Session isolation and agent singleton | ADR-0002; Q-003 | WTS enumerated session 0 (disconnected) and session 2 (active). `WTSQueryUserToken(2)` failed with error `1314` (`ERROR_PRIVILEGE_NOT_HELD`) under the current medium token. A named mutex reported first-created `True` and second-created `False`. | `VERIFIED` constraints; service supervision `BLOCKED`; mechanism `RECOMMENDED` |
| P3-S11 | Store accounts, extension ID, signing | Q-004; ADR-0004; ADR-0010 | Fixed unpacked ID and HKCU host registration verified. Store accounts, store-assigned ID confirmation, and Authenticode certificate remain external. | `BLOCKED` (external) for store/signing; ID portion `VERIFIED` |
| P3-S12 | Full-mode supervision loop design | ADR-0002; Q-003 | Logon-trigger task registered via Task Scheduler COM API non-elevated (`schtasks /create` denied, COM API allowed); agent launched by Task Scheduler in session 2; singleton second instance exited code 7; heartbeat at 500 ms; agent kill detected after 3 missed beats and restarted via the task (~12.6 s kill-to-healthy); supervisor kill did not kill the agent; restarted supervisor adopted the same agent without duplicate; crash-loop produced exactly 3 restarts in ~37 s then `RESTART LIMIT REACHED -> degraded` flag with zero agent processes. | `VERIFIED` for the supervision loop in user mode; session-0 service execution `BLOCKED` (needs real SCM service binary and elevation) |
| P3-S13 | Full-mode installer lifecycle | ADR-0007; ADR-0010; Q-002 | Full-mode script compiled successfully. Elevated silent install exit 0; files in `Program Files (x86)`; service `BoundlessStudySpikeSvc` created (`WIN32_OWN_PROCESS`, `AUTO_START`, LocalSystem, correct binPath); HKLM Native Messaging keys written **under Wow6432Node** (32-bit redirect); non-elevated `sc start`/`sc delete` denied error 5; elevated silent uninstall exit 0 removed the service (error 1060), install dir, and HKLM keys, and preserved user data with a marker. | `VERIFIED` for elevated install/uninstall/service-registration/removal and data preservation; registry redirection is a required correction; service start/stop with a real service binary, Limited-to-Full upgrade, repair, rollback `NOT EXECUTED` |
| P3-S14 | EF Core 10 + Microsoft.Data.Sqlite integration | ADR-0005; ADR-0006 | All ten scenarios executed (see §3.11): creation, both migrations, handshake, tamper detection, transactions, concurrency, backup/restore, WAL, interrupted migration, v1→v2 upgrade. Interrupted-migration finding: rollback clean; unaided recovery blocked on a stale `__EFMigrationsLock` row and completed (3.2 s, data preserved) only after manual lock clearing. | `VERIFIED` (details, model/migration mismatch finding, and migration-lock crash-safety finding R-P3-16 in §3.11) |
| P3-S15 | Store publishing requirements | Q-004 | Chrome Web Store: one-time registration fee, developer email, dashboard upload; official workflow documents keeping the development ID in production via the manifest `key` (upload → view public key → embed). Edge Add-ons: Partner Center, MSA required, no registration fee, individual/company accounts, verification takes days to weeks for companies. | `DOCUMENTED`; account creation and review remain external |
| P3-S16 | Foreground/fullscreen/multi-monitor detection | ADR-0002; ADR-0003 | GuardProbe compiled and executed: 1 monitor (1920x1200 primary) enumerated; foreground process/title identified; fullscreen loop ran with 0 positive samples (no fullscreen window in the run); detection latency at 100 ms polling over 5 s = 46 samples, avg 5.349 ms, max 8.002 ms. | `VERIFIED` at PoC level for enumeration/foreground/latency; fullscreen-positive case and full enforcement behavior stay product-phase |

## 3. Technical Evidence and Results

### 3.1 Compatibility — P3-S1

Official statements used:

- .NET 10 supported OS (last updated 2026-09-28): Windows 11 26H1, 25H2, 24H2 (including IoT/E variants), 23H2 (E), Windows 10 21H2 (E/IoT), 1809 (E), 1607 (E); Arm64, x64, x86.
- Microsoft Edge supported OS (updated 2026-11-09): Windows 11; Windows 10 SAC 1709 and later; Windows 10 Enterprise LTSC 2015/2016/2019/2021; Windows 11 Enterprise multi-session; Windows 11 IoT Enterprise 2024 LTSC.
- Chrome system requirements: Windows 10 and up on Intel with SSE3; Windows 11 and up on ARM.

Local smoke results:

- Windows 11 26H2 build `26300.9974` x64 runs .NET 10 runtime `10.0.14` and both browsers.
- Chrome `157.0.8103.51` and Edge `157.0.4341.46` operate on this build.
- This local result is not an official support claim for 26H2 because the .NET 10 supported-OS page does not list it.

### 3.2 Browser MV3, fixed ID, and DNR — P3-S2 and P3-S3

Extension manifest used a generated RSA public key in the `key` field. The ID was derived as the first 16 bytes of SHA-256 over the DER public key, mapped `0-f` to `a-p`:

`jjfgoeghdckpppcfjinhcccffmkmkibn`

Observed results:

- Edge: service worker target `chrome-extension://jjfgoeghdckpppcfjinhcccffmkmkibn/sw.js`; test page ran; manifest version 3.
- Chrome: same ID loaded through `Extensions.loadUnpacked`; service worker target present; test page ran.
- Dynamic rule:
  - `id: 1001`, `action: block`, `condition.urlFilter: ||example.com^`, `resourceTypes: main_frame`.
  - `testMatchOutcome({url: "https://example.com/page", type: "main_frame"})` returned `matchedRules: [{ruleId: 1001, rulesetId: "_dynamic"}]`.
- Session rule:
  - `id: 2001` for `||session.example^` existed immediately after creation.
- Edge restart:
  - `dynamicBefore` on the second launch contained rule 1001.
  - `sessionAfterRestart` was empty.
  - Native Messaging reconnected and returned pong.
- Chrome restart:
  - Branded Chrome 157 ignored `--load-extension`; the CDP unpacked-loading path loads the extension per launch. Dynamic-rule persistence across a Chrome restart was therefore not proven locally.
  - Chromium documentation states dynamic rules persist across browser restarts; session rules are cleared. This remains `DOCUMENTED` for Chrome.

Limitations:

- Headless browser run; no interactive browser UI.
- No agent/bridge heartbeat, focus-session policy synchronization, policy-push failure, or extension-disabled state tested.
- No store-installed CRX; the fixed manifest `key` proves development ID stability, not the final store-assigned ID.

### 3.3 SQLite — P3-S5

Environment: Python `3.12.10`, SQLite `3.49.1`, WAL mode, `synchronous=NORMAL`.

| Test | Observed result |
|---|---|
| Journal mode | `wal` |
| Read during open write | Reader saw 1 committed row while 100 uncommitted rows were in the writer transaction |
| After commit | Reader saw 101 rows |
| Snapshot inside one transaction | 101 before, 101 after another writer committed 25 rows; 126 after the read transaction ended |
| Zero-timeout writer | `database is locked` |
| Bounded wait | Waiter succeeded after `0.657 s` with a 5-second busy timeout |
| Online backup during writes | `integrity_check=ok`; 240 rows; source 240 at completion |
| Crash before commit | Child exited 1; 500 uncommitted rows invisible; seed present; `integrity_check=ok` |
| Crash after commit | Child exited 0; 500 committed rows plus marker visible; `integrity_check=ok` |
| WAL checkpoint | `PRAGMA wal_checkpoint(TRUNCATE)` returned `[0,0,0]`; WAL file 0 bytes |

Limitations:

- This validates the SQLite engine, not `Microsoft.Data.Sqlite` or EF Core 10.
- No EF Core migration, model mapping, `DbContext` concurrency, or packaged-runtime test.
- Single local user; no multi-user profile or antivirus interference test.
- The backup was a consistent SQLite snapshot, not a full application backup/restore workflow with checksums and schema-version validation.

### 3.4 Ledger idempotency — P3-S6

| Test | Observed result |
|---|---|
| First reward | `session:1:xp` inserted; source marked rewarded; 1 ledger row; 25 XP |
| Duplicate | `UNIQUE constraint failed: ledger.idempotency_key`; still 1 row and 25 XP |
| Simulated failure | Reward and source update rolled back; no `session:2:xp` row |
| Concurrent duplicate | One writer committed; one received the unique-constraint error; exactly 1 row for `session:3:xp`; total 35 XP |

This supports the ADR-0006 unique-key and atomic-transaction conditions at the database level. The domain transaction boundary, achievement fan-out, daily cap, and economy-version behavior were not implemented or tested.

### 3.5 Named pipes — P3-S7 and P3-S8

Node byte-mode harness (framing, concurrency, resilience):

| Test | Observed result |
|---|---|
| Single request | `pong` with matching request ID |
| Concurrency | 10 clients, 200 requests, all correlated, 17 ms |
| Timeout | Client timed out on a 2-second server operation after 300 ms |
| Connection survival | A subsequent ping succeeded on the same connection |
| Cancellation | `cancel` returned `cancelled` |
| Malformed frame | 2 MB length was rejected; connection closed |
| Server restart | Client reconnected and pinged successfully |
| Server stopped | New connection failed |

.NET Framework message-mode harness:

- `MODE=Message`
- DACL: `D:P(A;;0x1f019f;;;SY)(A;;0x1f019f;;;S-1-5-21-...-1001)`
- DACL protected (`D:P`), with only SYSTEM and the current user SID.
- Two back-to-back frames read as `first` and `second`, with `IsMessageComplete=True` for both.
- Response `ok` returned to the client.

Limitations:

- Node’s Windows pipe implementation is byte-mode; message-mode was verified separately with .NET Framework.
- Cross-user access denial was not executed because no second user token was available. The protected DACL construction and the Windows access-check model support denial, but that is `DOCUMENTED`, not experimentally proven.
- Client identity/SID validation, per-role authorization, 1 MB boundary, backpressure, and rate limits were not implemented in the harness.

### 3.6 Installer — P3-S9

Tool: Inno Setup 6.0.5 command-line compiler, extracted into the temp area. This is a test compiler, not the recommended product version.

Limited-mode sequence:

1. Built `BoundlessStudySpike-1.0.0.exe`.
2. Installed silently with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER`.
3. Exit code 0.
4. App installed to `%LocalAppData%\Programs\BoundlessStudySpike`.
5. HKCU Chrome and Edge Native Messaging host keys pointed to the installed manifest.
6. HKCU `Run` value pointed to the installed stub.
7. Uninstall entry displayed version `1.0.0`.
8. Seed data created at `%LocalAppData%\BoundlessStudySpike\data\seed.json`.
9. No `C:\Program Files\BoundlessStudySpike` directory and no service were created.
10. Upgraded to `1.1.0` with the same AppId; exit code 0; user-modified seed data preserved; one uninstall entry; host path unchanged.
11. Uninstalled silently; exit code 0; app directory removed; HKCU host and Run values removed; uninstall entry removed.
12. Data directory and modified seed preserved; uninstall marker written.

Full-mode status (first round):

- The script compiles service creation and deletion commands gated by `IsAdminInstallMode` (`sc.exe create/delete`), but these were not executed in the first round.
- Per-machine path, HKLM registration, service account, service start/stop, repair, rollback, and signed-installer behavior were `BLOCKED` at that time.
- Closure round: the elevated Full-mode install/uninstall lifecycle with service registration/removal was executed and `VERIFIED` (§3.10); a 32-bit compiler registry-redirection correction was found (R-P3-13). Service start/stop with a real binary, repair, rollback, and signing remain not executed (§3.13 runbook).

Limitations:

- No clean VM, no elevation (first round), no service binary, no Authenticode certificate, no MSI, and no real application payload. (The closure round obtained elevation through the normal UAC prompt; see §3.10.)
- Inno Setup 6.0.5 differs from current Inno Setup 7.1.1; the product recommendation must be recompiled and re-tested with the selected current version.
- “Repair” and “rollback” were not tested; only install, upgrade, and uninstall.

### 3.7 Service, session, and agent singleton — P3-S10

Observed:

- `WTSEnumerateSessions` returned two sessions:
  - Session `0`, station services, state disconnected.
  - Session `2`, interactive station, state active.
- Current process session: `2`.
- `WTSQueryUserToken(2)` returned `False`, error `1314` (`ERROR_PRIVILEGE_NOT_HELD`).
- Current token has no `SeTcbPrivilege`; only `SeShutdownPrivilege` was present.
- Process is not elevated; Administrators is deny-only.
- Named mutex `Local\BoundlessStudyPhase3Agent`: first create returned `createdNew=True`; second create in the same process returned `False`.

Interpretation:

- A Session 0 service cannot show the interactive overlay or own the interactive desktop.
- Launching an agent into an active user session requires a privileged mechanism (for example a LocalSystem service using WTS/CreateProcessAsUser, or a per-user Task Scheduler task managed by the service).
- The per-user agent singleton can be enforced with a named mutex.
- Full-mode service installation, logon/logoff, fast user switching, sleep/resume, reboot, and restart rate limiting were not executed in this round. (Closure round: the supervision loop including restart rate limiting was verified in user mode — §3.9; the session-0 service items remain — §3.13 runbook.)

Recommended Full-mode supervision design (`RECOMMENDED`, not verified):

1. Install a LocalSystem service with a minimal surface and no network access.
2. Install a visible per-user Task Scheduler task at logon for each user, or register it at first run; the task runs the agent as the interactive user.
3. The service enumerates active WTS sessions and ensures the matching agent task/process is running.
4. The agent exposes a control pipe and heartbeat; the service records health and detects unexpected exit.
5. Restart is rate-limited with backoff and a visible failure state; no hidden persistence and no kernel driver.
6. On logoff, the agent shuts down cleanly; the service does not keep a user process alive across sessions.
7. If the service cannot supervise reliably, revise the Full-mode promise rather than hiding the failure, as required by ADR-0002.

Why not the alternatives:

- `CreateProcessAsUser` directly from the service is viable but requires holding and correctly using `SeTcbPrivilege`; it increases the privileged code surface.
- A per-user-only tray app cannot provide independent supervision.
- A kernel driver or process-termination mechanism is outside the approved architecture.

### 3.8 Distribution and signing — P3-S11

Verified locally:

- A deterministic extension ID from a manifest `key`.
- HKCU Native Messaging registration for Chrome and Edge.
- Host launch and framed ping/pong.
- Host allowlist bound to the fixed extension ID.

External or unverified:

- Chrome Web Store and Edge Add-ons developer accounts, identity verification, review, and publication.
- Final store-assigned extension ID; the host manifest allowlist must be updated after the first store upload if it differs.
- Chrome/Edge policy push, enterprise force-install, and school-managed browser restrictions.
- Heartbeat, focus-session synchronization, extension-disabled detection, and policy-failure states.
- Authenticode certificate, timestamping, SmartScreen reputation, and signed installer/service binaries.
- Store privacy disclosures and permission justifications.

Closure round: the concrete requirements for both stores (accounts, fees, ID-stability workflow, review, verification timelines) are now `DOCUMENTED` with owners, prerequisites, and completion conditions in §3.12. The unverified items above remain external or product-phase.

### 3.9 Full-mode supervision loop — P3-S12 (closure round, 2026-11-13)

The proposed design is a LocalSystem service supervising a per-user agent. `sc create` from a non-elevated shell fails with error 5 (`Access is denied`), and a real SCM service binary does not exist in a spike, so the **service role was simulated by a user-mode supervisor process** running the identical supervision loop: real Task Scheduler task, real agent process, real named pipes, real crash/restart paths. Session-0-only effects remain `BLOCKED` (§3.13 runbook).

Task registration finding:

- `schtasks /create /sc onlogon` → `Access is denied` non-elevated.
- `Register-ScheduledTask` (Task Scheduler 2.0 COM API) → succeeded non-elevated for a user-owned task with a `LogonTrigger` for the current user, `Interactive only`, no execution time limit.
- Task XML evidence: `LogonTrigger` with `UserId=<machine>\<user>`, command = spike agent, arguments `--spawned-by-task`.

Observed loop results:

| Scenario | Observed result |
|---|---|
| Task-run launch | `schtasks /run` exit 0; agent started by Task Scheduler in interactive session 2 |
| Singleton | A manually launched second agent exited with code 7 (per-user mutex held); the task-launched agent kept running |
| Heartbeat | Supervisor heartbeated every 500 ms over the control pipe; agent replied with its PID and a monotonic sequence number |
| Agent kill | 3 consecutive missed beats (3 s connect timeout each); `taskkill` + task re-run; new agent PID answering ~12.6 s after the kill |
| Supervisor kill | Agent survived; a restarted supervisor detected the live agent via heartbeat and adopted it — no duplicate process, heartbeats resumed |
| Crash loop | With a defect flag, each restart crashed immediately: exactly 3 restart attempts (60 s window, limit 3), then `RESTART LIMIT REACHED -> degraded mode` flag written, zero agent processes, no further restarts |

Timing parameters used (spike values, to be re-tuned in the product): heartbeat 500 ms, heartbeat timeout 3 s, failure threshold 3, restart limit 3 per 60 s.

Interpretation:

- The supervision design (ensure task → launch through Task Scheduler → heartbeat → rate-limited restart → visible degraded state) works end-to-end at the mechanism level.
- The single-instance mutex is a genuine cross-process singleton (earlier P3-S10 tested only in-process).
- A user-mode supervisor is NOT the LocalSystem service. The remaining service-only behaviors (starting the task from session 0 via `WTSQueryUserToken`, `SERVICE_CONTROL_SESSIONCHANGE` handling for logon/logoff/FUS, sleep/resume and reboot survival of the service itself, multi-session fan-out) require a real service binary on an elevated machine and stay `BLOCKED`, now with a precise owner list (§3.13 runbook).

### 3.10 Full-mode installer — P3-S13 (closure round, 2026-11-13)

New script: `PrivilegesRequired=admin`, per-machine directory, HKLM Native Messaging registration, `sc.exe create/start/delete` service lifecycle, user-data preservation. Compiled successfully with the Inno 6.0.5 test compiler.

Executed (elevation granted through the normal UAC prompt; `ConsentPromptBehaviorAdmin=5`, `EnableLUA=1`):

| Step | Result |
|---|---|
| Elevated silent install | Exit 0; binaries in `C:\Program Files (x86)\BoundlessStudySpike` |
| Service creation | `BoundlessStudySpikeSvc` existed after install: `WIN32_OWN_PROCESS`, `AUTO_START`, `BINARY_PATH_NAME` = stub path, `SERVICE_START_NAME` = LocalSystem |
| HKLM Native Messaging | Keys written — but under `HKLM\SOFTWARE\WOW6432Node\...` for both Chrome and Edge |
| Non-elevated boundaries | `sc start` → error 5; `sc delete` → error 5 |
| Elevated silent uninstall | Exit 0; service gone (error 1060 on query), install dir removed, both HKLM keys removed, `%LocalAppData%\BoundlessStudySpike\data` preserved with marker |

New risk discovered and recorded (R-P3-13): the 32-bit Inno Setup compiler redirected HKLM writes into `Wow6432Node`. A 64-bit browser reads the native registry view; a 32-bit-only HKLM registration could be missed. Correction required for the product: build with Inno Setup 7.x 64-bit install mode (`64bit` install dir and `64:` registry prefixes or the x64 compiler build) and re-verify keys land in the native view.

Not executed (remains in the elevated runbook, §3.13): service start/stop with a real SCM service binary, Limited-to-Full upgrade, repair flow, rollback after failure.

### 3.11 EF Core 10 and Microsoft.Data.Sqlite — P3-S14 (closure round, 2026-11-13)

Prerequisite solved: no .NET SDK was machine-installed, but `dotnet-install.ps1 -Channel 10.0 -InstallDir <temp>` installed SDK `10.0.404` **per-user** into the temp area (no elevation, no PATH change, no machine state). Tool versions: `Microsoft.EntityFrameworkCore.Sqlite` 10.0.x, `Microsoft.EntityFrameworkCore.Design` 10.0.x, `dotnet-ef` 10.0.15.

Isolated net10.0 console spike over a representative model subset (Tasks, FocusSessions, SessionEvents, Ledger with unique idempotency key; v2 adds Achievements and a `Reason` column). Results:

| Scenario | Observed result | Status |
|---|---|---|
| 1. Database creation | Both migrations applied on a fresh file; tables + `__EFMigrationsHistory` + `__EFMigrationsLock` created; `integrity_check=ok` | `VERIFIED` |
| 2. Initial migration | `InitialCreate` applied; `Migrate()` path used | `VERIFIED` |
| 3. Subsequent migration | `AddAchievements` applied after `InitialCreate` on an existing database | `VERIFIED` |
| 4. Schema-version handshake | `__EFMigrationsHistory` vs model migrations compared; `OK` when consistent | `VERIFIED` |
| 4b. Tamper detection | Deleting the `AddAchievements` history row was detected as `MISMATCH` (exit 1) | `VERIFIED` |
| 5. Transaction behavior | Ledger + event in one explicit transaction committed together; simulated failure before commit left zero rows; duplicate idempotency key rejected (`Sqlite:19`) | `VERIFIED` |
| 6. Concurrent access | WAL reader saw the pre-write snapshot during an open write (1 row) and 51 after commit; isolation holds; a second connection opens (single-owner is architectural, as designed — documented honestly) | `VERIFIED` |
| 7. Backup/restore | `SqliteConnection.BackupDatabase` produced a consistent copy; simulated restore (copy back) passed `integrity_check` with data intact | `VERIFIED` |
| 8. WAL consistency | `journal_mode=wal` persisted; `wal_checkpoint(TRUNCATE)` returned 0 and shrank the WAL to 0 bytes | `VERIFIED` |
| 9. Interrupted migration | Two-part result. **(a) Rollback (first run):** child process killed during the v2 migration (4M-row workload): `integrity_check=ok`, migration fully rolled back (`__EFMigrationsHistory` unchanged, no partial tables), data preserved (1/1/1). **(b) Recovery re-run:** the re-run did **not** complete on its own — it blocked for 35.3 minutes at zero CPU waiting on a `__EFMigrationsLock` row that the killed child had committed at kill time (lock committed at 2026-11-13 05:17:36.9 UTC; SQLite rolls back the migration transaction but not the separately committed lock). After the blocked waiter was stopped and the stale lock row manually deleted, re-running the migration completed in **3.2 s** (exit 0): both migrations in history, `Achievements` present, `Reason` column added, tasks/ledger/events preserved (1/1/1), `integrity_check=ok`, the 4M-row `MigrationWorkload` table fully populated (4,000,000 rows — spike scaffolding, not schema). | Rollback `VERIFIED`; recovery `VERIFIED` only after manual stale-lock clearing; indefinite block on stale lock recorded as new risk R-P3-16 |
| 10. Upgrade from earlier schema | v1-only database seeded with raw SQL, then upgraded to v2: tasks/ledger preserved (1→1), `Achievements` table and `Reason` column added, handshake `OK` | `VERIFIED` |

Honest findings:

1. A compiled EF model newer than the applied migrations cannot seed an older-schema database through EF entities (v2 entity insert into a v1-schema table failed with `no column named Reason`). Schema-version gating must happen before any data access — exactly the handshake the spike implements.
2. Single database ownership is architectural. SQLite permits additional connections; the agent-only access boundary must be enforced by product design (as ADR-0005 states), and the spike documents that a second connection opens.
3. EF Core wraps each migration in a transaction on SQLite — the interrupted-migration result depends on this and is now engine-verified.
4. **The EF Core migration lock is not crash-safe.** A process killed mid-migration leaves its committed `__EFMigrationsLock` row behind (the migration transaction rolls back; the lock does not), and the next migration attempt waits indefinitely with no timeout. Product implication: before any migration, the agent must detect a stale lock (lock row present while no other owner is alive) and clear it, or apply a bounded wait with an explicit recovery step. This is risk R-P3-16.

This closes the previous `BLOCKED` on EF Core integration from the first validation round.

### 3.12 Browser store publishing — P3-S15 (closure round, 2026-11-13)

`DOCUMENTED` from official vendor documentation:

| Topic | Chrome Web Store | Microsoft Edge Add-ons |
|---|---|---|
| Developer account | Developer dashboard; one-time registration fee; dedicated developer email (email cannot be changed later); region allowlist | Partner Center; Microsoft account (MSA) as primary owner (GitHub login creates an MSA); **no registration fee** |
| Account types | n/a (single developer account model) | Individual and Company; company verification days-to-weeks; company approver contact required |
| Extension ID stability | Officially supported: upload the zip without publishing, view the public key in the Package tab, embed it in the manifest `key`; the extension ID then matches the dashboard Item ID | Same Chromium mechanism (ID derived from the signing key); Partner Center upload assigns the ID |
| Manifest key handling | `key` documented as "specifies your extension's ID for various development use cases" | Same |
| Signing | The store signs the package; no developer-side Authenticode for the extension itself | Same |
| Update behavior | Store-hosted updates via the store; `update_url` only for self-hosted (not used in MVP) | Store-hosted |
| Native Messaging host registration | Documented: HKCU (per-user) or HKLM (whole-machine) registry keys pointing to the host manifest | Same Chromium mechanism; verified in P3-S4 |

Account-dependent tasks (owner: user; prerequisite: account and identity/fee; completion condition: first store upload + confirmed ID):

1. Create the Chrome Web Store developer account and pay the one-time fee.
2. Create the Edge Partner Center (MSA) developer account; choose Individual or Company.
3. Upload the extension zip without publishing; record the assigned Item ID (Chrome) and compare with the manifest-key ID; do the same in Edge.
4. Complete store review; verify Native Messaging allowlist against the confirmed final ID.
5. Acquire the Authenticode code-signing certificate (external vendor; budget decision).

### 3.13 Enforcement boundaries and elevated runbook — P3-S16 + P3-S12 remainder

Guard-boundary PoC results (user-mode, non-elevated; `GuardProbe` compiled with the C#5 compiler after two compatibility fixes — pre-declared `out` variables — and executed 2026-11-13, exit 0):

- Monitor enumeration from a standard user process works: 1 monitor detected, `1920x1200`, flagged primary (Win32 `EnumDisplayMonitors`).
- Foreground identification from a standard user process works: process `WindowsTerminal`, window title read via `GetWindowText`, PID resolved via `GetWindowThreadProcessId`.
- Fullscreen/borderless-maximized detection loop (foreground rect vs each monitor rect) executed; in this run no fullscreen window was on screen, so the detection produced 0 positive samples — the positive path was exercised as a negative check only.
- Detection latency at 100 ms polling, measured over 5 s in two independent runs: run 1 — 46 samples, average `5.349 ms`, max `8.002 ms`; run 2 — 45 samples, average `3.781 ms`, max `29.340 ms` (single scheduling outlier). The product enforcement budget must include the overlay and policy-application cost on top of this.
- What remains product-phase (not proven here): overlay rendering, enforcement actions, timing under load, the fullscreen-positive case in a live fullscreen window, recovery after process termination inside the real agent (the supervision restart path was proven separately in §3.9), and anti-tamper guarantees — none are claimed.

Elevated runbook for the remaining Full-mode items (owner: user; prerequisite: elevated session or clean Windows 11 VM; completion condition: all steps recorded):

1. Build a real service binary (Phase 4 product code) — the spike stub is not an SCM service.
2. Elevated: install the Full-mode build; confirm HKLM keys appear in the **native** registry view (not `Wow6432Node`) with Inno 7.x 64-bit mode.
3. Elevated: `sc start/stop` the real service; verify the service launches the per-user Task Scheduler task at logon and on demand.
4. Interactive: logon/logoff, fast user switching, sleep/resume, reboot; verify the agent follows sessions and the service survives.
5. Simultaneous two-user sessions (RDP): verify per-user agents and per-user pipes.
6. Limited→Full upgrade over an existing Limited install with data; repair flow; rollback after a forced failure.
7. Re-run the §3.9 supervision scenarios with the real service in session 0.

## 4. Architecture Decision Updates

No ADR status was changed without user approval. Evidence sections were added to the relevant ADRs.

| ADR | Current status | Phase 3 evidence | Disposition | Required action |
|---|---|---|---|---|
| ADR-0002 — per-user agent, service supervises | Proposed | Session 0/2 separation, `WTSQueryUserToken` error 1314, agent mutex; **closure round:** supervision loop (task launch, heartbeat, restart, adoption, rate-limited degraded state) verified in user mode; session-0 service execution still unproven | Supervision mechanism `VERIFIED` at loop level; session-0 service execution `BLOCKED` | Run the elevated runbook §3.13 with a real service binary before any Full-mode claim |
| ADR-0004 — Chromium MV3 and DNR | Proposed | MV3, fixed ID, dynamic/session rules, Native Messaging in Chrome and Edge; Edge restart persistence; **closure round:** store ID-stability workflow documented | Evidence supports acceptance with conditions; status unchanged | Confirm store ID after first upload; heartbeat/policy integration |
| ADR-0005 — SQLite single writer | Accepted with conditions | WAL, snapshot, busy timeout, online backup, crash recovery verified at engine level; **closure round:** EF Core 10 migrations, handshake, tamper detection, transactions, concurrency, backup/restore, WAL, interrupted migration, v1→v2 upgrade all `VERIFIED`; migration-lock crash-safety gap found (R-P3-16) | Conditions on EF Core integration now validated; single-owner boundary confirmed architectural | Adopt model-version gating and stale-lock detection/clearing before migration |
| ADR-0006 — append-only events and ledger | Accepted with conditions | Unique idempotency key, atomic rollback, concurrent duplicate prevention verified at SQLite level; **closure round:** EF Core transaction-level idempotency and rollback verified | Conditions remain for domain transaction code | Test achievement fan-out, daily cap, and economy-version behavior in Phase 5 |
| ADR-0007 — Full/Limited modes | Accepted | Limited-mode install/upgrade/uninstall and HKCU registration verified; **closure round:** elevated Full-mode install, service registration, elevated uninstall with data preservation `VERIFIED`; Wow6432Node registry redirection requires a 64-bit-mode correction | Full-mode lifecycle partially proven; upgrade/repair/rollback and real-service lifecycle not executed | Elevated runbook §3.13 steps 2, 6 |
| ADR-0010 — provisional packaging | Proposed | Inno Setup Limited-mode install verified; **closure round:** Full-mode script compiled and elevated lifecycle executed; 32-bit compiler registry redirection discovered | `RECOMMENDED` Inno Setup 7.x 64-bit; status unchanged | Approve installer direction with 64-bit mode; validate upgrade/repair/rollback and signing |

### 4.1 Installer comparison

| Candidate | Strengths | Weaknesses / unknowns | Phase 3 evidence |
|---|---|---|---|
| Inno Setup 7.x | Single EXE; per-user and admin modes; HKCU/HKLM; service via `sc.exe`; upgrade/uninstall; mature | Not MSI; service lifecycle must be scripted; signing external | Limited mode `VERIFIED` with 6.0.5; elevated Full-mode install/uninstall lifecycle executed with 6.0.5 (§3.10) — 64-bit mode required for HKLM keys; upgrade/repair/rollback not executed |
| WiX v5/v6 + Burn | MSI/enterprise-friendly; service install; bundle can chain per-user and per-machine MSIs | Two packages or a bundle; more complexity; no local toolchain; not tested | `DOCUMENTED` only |
| NSIS | Small single EXE; per-user install possible | Runtime privilege switching needs separate builds or a UAC plugin; less natural dual-mode story; no local toolchain | `DOCUMENTED` only |

`RECOMMENDED`: use Inno Setup 7.x for the MVP, with a single signed EXE and explicit Full/Limited paths. Keep WiX/Burn as the fallback if MSI or enterprise deployment becomes a requirement. Do not treat this as a verified final selection until Full mode is executed on a clean elevated VM.

## 5. Compatibility Matrix

### 5.1 Windows

| Platform | Vendor support | Local evidence | MVP support statement |
|---|---|---|---|
| Windows 11 24H2 x64 | .NET 10 lists 24H2 (including E/IoT variants); Edge and Chrome support Windows 11 | Not installed/tested locally | Supported baseline |
| Windows 11 25H2 x64 | .NET 10 lists 25H2; Edge and Chrome support Windows 11 | Not installed/tested locally | Supported baseline |
| Windows 11 26H1 x64 | .NET 10 lists 26H1; Edge and Chrome support Windows 11 | Not installed/tested locally | Supported baseline |
| Windows 11 26H2 x64 | .NET 10 page (2026-09-28) does not list 26H2; Edge/Chrome support Windows 11 | Local .NET 10 runtime and both browsers operate | Conditional; add when Microsoft lists it; not a release claim yet |
| Windows 11 23H2 | .NET 10 lists 23H2 only as Enterprise/Education; Home/Pro EOL 2025-11-11 | Not tested | Not baseline; Enterprise/Education only if required |
| Windows 10 22H2 and earlier | .NET 10 lists only E/ESU/LTS builds; consumer 22H2 EOL; product assumption A-001 excludes Windows 10 | Not tested | Not supported in MVP |
| Windows 11 ARM64 | .NET and Chrome support ARM64; product assumption excludes ARM64 | Not tested | Not supported in MVP |
| Windows Server | .NET 10 supports several versions; product target is a student desktop | Not tested | Not supported in MVP |

Baseline for the release test matrix: **Windows 11 24H2, 25H2, and 26H1 on x64**. Add 26H2 after the .NET 10 support page lists it and a clean-VM test passes. Do not claim Windows 10 or ARM64 support.

### 5.2 Browsers

| Browser | Local version | Local evidence | Support statement |
|---|---|---|---|
| Chrome stable | `157.0.8103.51` | MV3 load via CDP unpacked path; fixed ID; DNR dynamic/session; Native Messaging | Supported channel: current stable at release; 157 verified locally |
| Edge stable | `157.0.4341.46` | MV3 load; fixed ID; DNR dynamic/session; Native Messaging; restart persistence | Supported channel: current stable at release; 157 verified locally |
| Older Chrome/Edge majors | Not tested | None | Not claimed; release test uses the then-current stable major |
| Other Chromium browsers | Not tested | None | Unsupported; report visibly |

### 5.3 Runtime and toolchain

| Component | Version / status | Implication |
|---|---|---|
| .NET | Runtime `10.0.14`; no machine-wide SDK. Closure round: per-user SDK `10.0.404` in the temp area | Product target `net10.0-windows` with WPF is documented; EF Core 10 validation executed in the closure round (§3.11) |
| SQLite engine | `3.49.1` via Python; EF Core `Microsoft.EntityFrameworkCore.Sqlite` 10.0.x + `Microsoft.Data.Sqlite` via the spike | Engine behavior verified; provider/EF behavior verified in §3.11; final package versions to be pinned at Phase 4 start |
| Windows SDK | Absent | Signing, manifests, and some packaging checks `BLOCKED` |
| Inno Setup | 6.0.5 test compiler | Limited-mode and elevated Full-mode mechanics verified; product must use 7.x 64-bit mode and re-test (R-P3-13) |
| Node/Python | 26.12.1 / 3.12.10 | Validation-only tools; not product dependencies |
| Windows App SDK / MSIX | Not used | Out of current packaging direction |

### 5.4 Q-006 disposition

**Resolved with conditions (DOCUMENTED + local smoke):**

- Windows 11 x64 24H2, 25H2, and 26H1 are the supported baseline.
- Windows 11 26H2 is conditional until officially listed by .NET 10 and clean-VM tested.
- Windows 10, Windows 11 ARM64, and Windows Server are out of MVP scope, consistent with assumption A-001.
- Chrome stable and Edge stable are the only supported browsers; the release test uses the current stable major at release.
- The local Chrome/Edge 157 results are evidence for those versions, not a permanent version floor.

## 6. Risk Register

| ID | Risk | Impact | Likelihood | Mitigation / status |
|---|---|---|---|---|
| R-P3-01 | Full-mode service supervision cannot be validated here | High — Full-mode promise unproven | Reduced: loop verified in user mode; session-0 execution remains unproven | Elevated runbook §3.13 steps 3–7 with a real service binary before any Phase 4 Full-mode claim |
| R-P3-02 | Full-mode installer, service install/removal, and Limited-to-Full upgrade unvalidated | High — release path unproven | Reduced: elevated install/uninstall/service registration/removal `VERIFIED` (§3.10); upgrade unvalidated | Elevated runbook §3.13 step 6; ADR-0007 gate remains |
| R-P3-03 | EF Core 10 migrations, `Microsoft.Data.Sqlite`, backup/restore workflow untested | High — data safety | Closed 2026-11-13: all executed scenarios `VERIFIED` (§3.11) | Product must adopt the schema-version handshake gating pattern; final package versions pinned at Phase 4 start |
| R-P3-04 | Store-assigned extension ID differs from the manifest-key ID | Medium — Native Messaging allowlist breaks | Medium | Confirm after first store upload; update host manifest; keep ID in one build variable |
| R-P3-05 | Chrome unpacked harness cannot prove restart persistence | Low — product store installs behave differently | Medium | `DOCUMENTED`; re-test store-installed CRX later |
| R-P3-06 | No Authenticode certificate or signing tool | High — public release blocked | High | External dependency; budget and acquire before release |
| R-P3-07 | Windows 11 26H2 not yet on .NET 10 support list | Medium — support claim risk | Medium | Conditional support; re-check vendor page; clean-VM test |
| R-P3-08 | Cross-user pipe ACL denial not executed | Medium — security claim unproven | Medium | Elevated/multi-user test; keep DACL construction and SID checks |
| R-P3-09 | Full-mode LocalSystem service increases privileged surface | Medium — security | Medium | Minimal service surface, no network, code review, least-privilege analysis |
| R-P3-10 | School browser policy blocks extension or Native Messaging | Medium — Limited-mode degradation | Medium | Visible degraded state; onboarding diagnostics; documentation |
| R-P3-11 | Inno Setup dual-mode script complexity and service scripting | Medium — installer defects | Medium | Prototype Full mode on VM; test repair/rollback; sign and timestamp |
| R-P3-12 | Validation evidence lives in temp, not the repo | Low — reproducibility | Low | Commands and key outputs recorded here; no product dependency on temp files |
| R-P3-13 | 32-bit Inno writes HKLM Native Messaging keys under `Wow6432Node`; 64-bit browsers may miss them | High — Native Messaging breaks in Full mode | High (with 32-bit compiler) — avoidable | Use Inno Setup 7.x 64-bit install mode / `64:` registry prefixes; verify keys in the native view (runbook §3.13 step 2) |
| R-P3-14 | A compiled EF model newer than the applied schema cannot access an older database | Medium — crash on schema mismatch | Medium | Schema-version handshake must gate all data access before first query (spike-proven pattern) |
| R-P3-15 | User-mode supervision evidence does not cover session-0-only behaviors | Medium — Full-mode claims overstated if ignored | Medium | Elevated runbook §3.13 steps 3–7 with a real service binary |
| R-P3-16 | EF Core migration lock (`__EFMigrationsLock`) survives a mid-migration process kill; the next migration attempt blocks indefinitely | High — data upgrade permanently stalls after a crash | Medium — deterministic trigger, clear fix | Before migrating: detect a stale lock row and clear it (or bounded wait + explicit recovery step); spike-verified that clearing the lock lets the migration complete with data preserved (§3.11) |

## 7. Open Questions Register

| ID | Status after Phase 3 | Disposition |
|---|---|---|
| Q-001 | Open; visual-only blocker | Unchanged. Blocks final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing only. No visual work was performed. |
| Q-002 | Provisionally resolved; user approval required | `RECOMMENDED` Inno Setup 7.x (64-bit install mode — closure-round finding R-P3-13). Limited mode `VERIFIED`; elevated Full-mode install/uninstall/service registration/removal/data preservation `VERIFIED` (closure round). Upgrade/repair/rollback and signing remain in the elevated runbook. Do not claim final selection until those pass. |
| Q-003 | Partially resolved; session-0 remainder deferred with owner | `VERIFIED` (closure round): per-user logon-trigger task registration via COM API, Task Scheduler launch, control-pipe heartbeat, agent crash → rate-limited restart → visible degraded state, supervisor-crash adoption all work. `BLOCKED` remainder: service execution in session 0 with a real SCM binary, logon/logoff/FUS/sleep/resume/reboot/multi-session behavior — owned by the user via the elevated runbook §3.13. |
| Q-004 | Partially verified; external dependencies remain | Fixed ID, HKCU registration, host launch, and ping/pong `VERIFIED`. **Closure round:** store requirements and the official ID-stability workflow `DOCUMENTED` (§3.12). Store accounts, final store ID, review, and code signing remain external with owner/prerequisites/completion conditions recorded. |
| Q-005 | Resolved | `ECONOMY-1` remains frozen; no change. |
| Q-006 | Resolved with conditions | Windows 11 x64 24H2/25H2/26H1; Chrome/Edge current stable; 26H2 conditional; Windows 10/ARM64/Server out of scope. |
| Q-007 | Resolved | Achievement-code and economy-version rules unchanged. |

## 8. Phase 3 Completion Report

### 8.1 What was tested

- Official compatibility statements for .NET 10, Edge, and Chrome.
- MV3 loading, fixed extension ID, DNR dynamic/session rules, and Native Messaging in Chrome and Edge.
- SQLite WAL concurrency, snapshots, busy timeout, online backup, crash recovery, and ledger idempotency.
- Named-pipe framing, concurrency, correlation, timeout/cancel, malformed frames, reconnect, message mode, and a user-scoped DACL.
- Limited-mode install, upgrade, uninstall, HKCU registration, and data preservation with a real Inno Setup installer.
- WTS session isolation, `WTSQueryUserToken` privilege failure, and agent mutex behavior.
- **Closure round (2026-11-13):** the Full-mode supervision loop in user mode (per-user logon-trigger task via the Task Scheduler COM API, Task Scheduler launch, control-pipe heartbeat, agent-crash restart, supervisor-crash adoption, rate-limited crash-loop → degraded state); the elevated Full-mode installer install/uninstall lifecycle with service registration/removal and data preservation; EF Core 10 integration on a per-user .NET 10 SDK (migrations, handshake, tamper detection, transactions, concurrency, backup/restore, WAL, interrupted migration, v1→v2 upgrade); browser-store publishing requirements; guard-detection boundaries and timing.

### 8.2 What actually worked

- Edge passed the complete browser chain, including restart persistence and Native Messaging reconnection.
- Chrome passed MV3 loading through the DevTools unpacked path, fixed ID, DNR rule creation/matching, and Native Messaging.
- SQLite passed every executed WAL, snapshot, backup, and crash-recovery check.
- Ledger duplicate prevention and rollback passed at the database level.
- Named-pipe framing, concurrency, timeout/cancel, malformed-frame rejection, and reconnect passed.
- Limited-mode installer install, upgrade, uninstall, HKCU registration, and data preservation passed.
- Session isolation and the privilege requirement for cross-session launch were demonstrated.
- **Closure round:** the supervision loop (task launch, heartbeat, crash restart, supervisor-crash adoption, rate-limited degraded state) passed in user mode; the cross-process singleton mutex passed; the elevated Full-mode install/uninstall lifecycle passed, exposing one correction (64-bit registry mode); every executed EF Core 10 scenario passed — interrupted-migration rollback was verified, the recovery re-run was found to block on a stale migration lock (new risk R-P3-16) and completed in 3.2 s with data preserved after the lock was manually cleared; store requirements were documented from official sources; the guard PoC passed — monitor enumeration, foreground identification, and detection latency measured across two independent runs.

### 8.3 What failed or was not proven

- Failed as configured: the 32-bit Inno compiler wrote HKLM Native Messaging keys under `Wow6432Node` (R-P3-13). The correction is defined (Inno Setup 7.x 64-bit mode) but not yet executed.
- Failed as observed: an unaided recovery re-run after an interrupted migration blocks indefinitely on the stale `__EFMigrationsLock` row left by the killed process (R-P3-16). Recovery succeeded only after the lock was manually cleared (3.2 s, data preserved). The product must implement stale-lock detection/clearing.
- Not proven: session-0 service execution — a real SCM service binary starting/stopping, launching the per-user task, logon/logoff/FUS, sleep/resume, reboot, and multi-session fan-out.
- Not proven: Limited-to-Full upgrade, repair flow, rollback, service start/stop with a real binary.
- Not proven: cross-user pipe denial (no second user token available), client identity checks against a hostile client, policy push, heartbeat in the real agent, focus-session synchronization, and browser-policy failure states.
- Not proven at product level: overlay rendering, enforcement actions, timing under load, anti-tamper guarantees (PoC-level detection only).
- Not proven: Chrome store-install persistence and the final store-assigned extension ID (accounts external).
- No clean-VM evidence exists.

### 8.4 What remains unknown

- Whether a real LocalSystem service reliably starts and restarts the agent across all supported Windows 11 builds in session 0 (the user-mode loop is proven; session-0 execution is not).
- Whether the service can supervise multiple simultaneous user sessions without keeping processes alive after logoff.
- Whether the Inno Setup 7.x 64-bit Full-mode build places HKLM keys in the native view and upgrades, repairs, rolls back, and uninstalls a real service cleanly.
- Whether the final store extension ID matches the development ID in practice, and whether store policy permits the required permissions.
- Whether school-managed browser policy permits the extension and Native Messaging in the target environment.
- Whether the EF Core spike results hold under the full production model and concurrent Phase 4+ workloads (the spike used a representative subset).
- Whether a stale migration lock after a real product crash can always be distinguished from a legitimate in-progress migration on the same machine (two agents are architecturally excluded by single-writer, but the product must still detect the case).

### 8.5 Decisions recommended

1. Use **Inno Setup 7.x with 64-bit install mode** as the provisional installer (R-P3-13 correction), with a single signed EXE and explicit Full/Limited paths; keep WiX/Burn as the MSI fallback. Requires user approval.
2. Use **LocalSystem service + per-user Task Scheduler task + control-pipe heartbeat + rate-limited restart** for Full-mode supervision — now loop-verified in user mode; do not use hidden persistence, a kernel driver, or process termination.
3. Keep **SQLite with a single-writer agent** and the **unique idempotency-key ledger**; EF Core 10 integration is now spike-verified, including the mandatory schema-version handshake gating all data access.
4. Keep **Chrome stable and Edge stable** as the browser matrix; confirm the store ID and permission set before release.
5. Keep **Windows 11 x64 24H2/25H2/26H1** as the baseline; treat 26H2 as conditional and exclude Windows 10/ARM64/Server.
6. Keep Q-001 visual-only; perform no visual design until the dedicated UI phase and supplied references.

### 8.6 Phase 3 exit criteria

| Criterion | Status |
|---|---|
| Written feasibility report for each required validation | Complete for everything executable in this environment (compatibility, browser, SQLite, IPC, installer Limited + elevated Full-mode lifecycle, session constraints, supervision loop, EF Core incl. interrupted-migration recovery, stores, guard PoC); session-0 service items and external accounts remain with the elevated runbook §3.13 and §3.12 owners |
| Service/agent approach accepted, revised, or rejected | `RECOMMENDED` design now loop-verified in user mode; session-0 remainder blocked with runbook; not user-approved |
| Installer technology selected | `RECOMMENDED` Inno Setup 7.x with 64-bit mode; not user-approved; upgrade/repair/rollback remain |
| Browser distribution constraints documented | Yes; store requirements `DOCUMENTED` (§3.12); accounts/signing external |
| Visual reference inventory recorded | Yes; no references supplied; visual work untouched |
| Architecture and ADRs updated before implementation | Yes; evidence sections added, no status changed without approval |
| User authorizes the next phase | Not granted; Phase 4 remains unauthorized |

### 8.7 Completion statement

Phase 3 technical work is complete to the extent this environment permits, including the 2026-11-13 closure round. It has produced reproducible evidence for the browser, SQLite, ledger, IPC, Limited-mode installer, session-isolation boundaries, and — in the closure round — the Full-mode supervision loop in user mode, the elevated Full-mode installer install/uninstall lifecycle, EF Core 10 integration (including interrupted-migration rollback and lock-cleared recovery), store publishing requirements, and guard-detection boundaries.

It has **not** proven session-0 service execution, Limited-to-Full upgrade/repair/rollback, the final store identity, or code signing. Two configured/observed failures were found and corrected in design: the 32-bit installer's `Wow6432Node` registry redirection (R-P3-13, resolved by requiring Inno Setup 7.x 64-bit mode) and the EF Core migration lock surviving a mid-migration process kill (R-P3-16, resolved by requiring stale-lock detection/clearing before migration).

These items are explicit deferred risks with named owners and a runbook (§3.12, §3.13), not hidden failures. Phase 3 should be treated as **conditionally complete**: the user must complete the elevated runbook items, approve Q-002, and accept the deferred risks before Full-mode claims or Phase 4 implementation. Phase 4 has not started.

Recommended next validation, when authorized (elevated runbook §3.13):

1. Build the real service binary; elevated: install the Full-mode build with Inno 7.x 64-bit mode and confirm HKLM keys in the native view.
2. Elevated: `sc start/stop` the real service; verify service-initiated per-user task launch, logon/logoff, fast user switching, sleep/resume, reboot, crash/restart rate limiting, and uninstall.
3. Two simultaneous user sessions (RDP): verify per-user agents and pipes.
4. Limited→Full upgrade over existing data; repair flow; rollback after a forced failure.
5. Create the Chrome/Edge store accounts, upload without publishing, and confirm the final store IDs (§3.12 tasks 1–4).
6. Acquire and test an Authenticode certificate and timestamping.

Stop here. Do not begin Phase 4 without explicit user authorization.
