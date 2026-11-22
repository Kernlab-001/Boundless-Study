# ADR-0002: Per-user agent owns data and enforcement UI; Windows service supervises

- **Status:** Proposed
- **Date:** 2026-11-12
- **Decision owner:** User approval required
- **Related:** System architecture, Full/Limited modes, data model

## Context

A Windows service runs in Session 0 and cannot reliably display interactive overlays or own a per-user SQLite database without creating permission and multi-user problems. At the same time, Full mode requires an independent background service that can supervise recovery and provide stronger enforcement reliability than a per-user process alone.

The product also needs the same core functionality in Limited mode, where no service can be installed.

## Decision

Use four processes:

1. **Per-user agent:** owns SQLite, the session state machine, timing, application guard, overlay, and browser-policy coordination. It is the sole database owner and the only process that opens it, for reads and writes.
2. **WPF desktop UI:** MVVM client that communicates with the agent through a user-scoped named pipe. It may close while the agent continues.
3. **Windows service:** Full mode only. Starts at boot, monitors the agent, and attempts documented recovery. It does not write user data or show UI.
4. **Native bridge:** stateless translator between browser Native Messaging and the agent.

The shared domain and application layers do not depend on the service. Full mode adds supervision; it does not change the ownership model.

## Consequences

- Full mode can be stronger without a LocalSystem service writing LocalAppData.
- Limited mode can run the same application core without a service.
- The service-to-agent restart path must be validated on Windows 11.
- Two processes must be installed and diagnosed, increasing platform complexity.
- The UI is not the source of truth; closing it cannot stop tracking.
- App blocking remains best effort because the overlay is an interactive-session mechanism.

## Alternatives considered

1. **Windows service owns UI and database:** Rejected due to Session 0, UI, ACL, and multi-user problems.
2. **Single per-user tray app only:** Rejected for Full mode because it provides no independent supervision.
3. **Service terminates blocked processes without an overlay:** Rejected as unsafe, data-loss-prone, and hostile.
4. **Kernel driver for enforcement:** Rejected as disproportionate, risky, and contrary to the product constraints.

## Review trigger

Review after the Phase 3 service/agent restart spike. If the service cannot reliably supervise the agent, either revise the Full-mode promise or replace the supervision mechanism. Do not hide the failure behind a “Full mode” label.

## Phase 3 validation evidence (2026-11-12)

- `VERIFIED`: Windows session 0 (services) and session 2 (interactive) are separate; the current process runs in session 2.
- `VERIFIED`: `WTSQueryUserToken` for the active session failed with error `1314` (`ERROR_PRIVILEGE_NOT_HELD`) under the non-elevated token, and the token has no `SeTcbPrivilege`.
- `VERIFIED`: a named mutex enforces one agent instance (`FirstMutexCreatedNew=True`, `SecondMutexCreatedNew=False`).
- `BLOCKED`: service installation, start/stop, logon/logoff, fast user switching, sleep/resume, reboot, and restart rate limiting were not executed.
- `RECOMMENDED`: a LocalSystem service plus a visible per-user Task Scheduler task, control-pipe heartbeat, and rate-limited restart. This is not experimentally verified.
- Disposition: supervision remains unproven. Do not claim Full-mode reliability until an elevated clean-VM spike passes. Status remains Proposed.

### Closure-round evidence (2026-11-13)

- `VERIFIED` in user mode: the proposed supervision loop works end-to-end — per-user logon-trigger task registered via the Task Scheduler COM API (`schtasks /create` with a logon trigger is denied non-elevated; the COM API is not), agent launched by Task Scheduler in the interactive session, control-pipe heartbeat at 500 ms, agent kill detected after 3 missed beats and restarted via the task (~12.6 s kill-to-healthy), supervisor crash did not kill the agent and a restarted supervisor adopted the running agent without a duplicate, and a crash loop produced exactly 3 rate-limited restarts followed by a visible degraded flag with zero agent processes.
- `VERIFIED`: the per-user agent mutex is a genuine cross-process singleton (second instance exited code 7).
- `BLOCKED` (session-0 only): starting the task from the LocalSystem service, `SERVICE_CONTROL_SESSIONCHANGE` handling, sleep/resume and reboot survival of the service, multi-session fan-out — require a real SCM service binary on an elevated machine (runbook in `07-phase-3/phase-3-validation-record.md` §3.13).
- Disposition: the recommended mechanism is now loop-verified; only the privileged execution surface remains unproven. Status remains Proposed pending the elevated runbook.
