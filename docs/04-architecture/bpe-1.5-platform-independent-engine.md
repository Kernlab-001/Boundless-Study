# BPE-1.5 Platform-Independent Protection Engine

## Scope and boundary

BPE-1.5 adds deterministic protection policy evaluation and distinct application/network provider contracts to the isolated `prototypes/Boundless.Protection.Engine` project. It is platform-independent simulation logic only. This assembly contains no AppLocker, WFP, firewall, Group Policy, process-control, service, or native Windows enforcement implementation. Decisions and simulated provider mutation results must not be described as active OS protection.

The engine and its projects are nested under the `/prototypes/` solution folder. Production Agent, Desktop, Service, Application, and Domain projects do not reference the prototype.

## Policy evaluation

`ProtectionPolicyEvaluator` accepts a caller-provided session snapshot, caller-captured process identity, target, evaluation time, and session-scoped rules. It validates structure and time bounds, requires exact subject SID/session ID/generation matches, checks application targets against the captured executable path and SHA-256, and returns `Restrict` for invalid, stale, mismatched, or unmatched inputs. Protected-target exclusions are evaluated before rules. Deterministic precedence is **Exception > Deny > Allow > default Restrict**, with priority and rule ID tie breaks within an effect.

The evaluator does not authenticate or discover the session, SID, PID, executable image, or process liveness. These values are untrusted inputs supplied by a future caller. Therefore this evaluator is not an authorization boundary. Windows path comparison is lexical and normalizes separators and dot segments; device namespace paths are rejected, and common System32/SysWOW64/Sysnative protected paths are catalogued. Junction/reparse-point, 8.3, hard-link, and arbitrary alias resolution is not available without OS inspection and is outside this milestone. The catalog is a simulation policy exclusion list, not a guarantee about every system process on Windows.

## Provider states and contracts

Application and network provider contracts are separated. Capability discovery returns `Simulated`, `Available`, `Unavailable`, `Unsupported`, or `Error`. The current adapters wrap only persistent simulated providers and return `Simulated` when their inspection succeeds. `CanClaimProtection` is hard-disabled in this milestone, including for caller-constructed `Available` statuses. Simulation capability flags describe simulator operations only, not OS readiness.

Missing providers return `Unavailable`; unsupported contracts or `NotSupportedException` return `Unsupported`; provider errors return `Error`. Canceled discovery propagates cancellation. No capability result from the current assembly can claim real enforcement.

## Recovery and audit

BPE-1.3 SQLite journal/provider durability, owner-scoped restore, state machine, cancellation behavior, and same-journal cross-process file-region lock remain in place. Apply outcomes are normalized so unexpected provider success-named outcomes cannot leave a failed state; partial restoration remains explicitly retryable; unknown provider kinds fail as invalid requests. SQLite audit operations are limited to fixed allowlisted operation codes, and records contain no path, SID, hash, credential, or free-form user data.

The lock coordinates cooperating processes using the same journal path on supported local Windows/Linux filesystems only. Distinct journal paths do not coordinate even if they point at the same policy database. Audit storage is append-only by API behavior, not tamper-proof. Journal and synthetic policy stores retain identity material in plaintext for exact recovery; no encryption-at-rest claim is made.

## Threat model

**Defended against in the simulation:** malformed policy inputs; wrong SID/session/generation; stale caller identity snapshots; mismatched application target; unexpected/unavailable/unsupported provider responses; duplicate/conflicting requests; provider drift; ownership collisions; partial writes/restores; process crashes in the isolated test worker; and audit fields that could otherwise carry arbitrary strings.

**Not defended or proven:** malicious callers can fabricate process/session snapshots; no OS process or SID authentication exists; path aliases and filesystem redirection are not resolved; network destination identity can change after evaluation; there is no native OS provider or protection broker; the audit file can be modified by a filesystem principal with write access; and local-file locking assumptions do not extend to network filesystems or macOS.

## Verification

All BPE tests use synthetic identities, temporary SQLite databases, and disposable test worker processes. A worker's intentional crash is confined to that synthetic test worker; the engine never terminates a protected application. Release build/test counts and NuGet advisory results are recorded in the local BPE-1.5 deliverables.
