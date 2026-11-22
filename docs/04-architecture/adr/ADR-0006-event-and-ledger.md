# ADR-0006: Append-only session events and reward ledger

- **Status:** Accepted with conditions
- **Date:** 2026-11-12
- **Decision owner:** User approval required for final acceptance
- **Related:** Domain rules, data model, gamification, ADR-0005

## Context

Boundless Study must survive crashes, restarts, mode upgrades, and repeated events without duplicating XP, tasks, or achievements. It also needs an audit trail that explains why a reward exists. Mutable counters and direct balance edits make duplicate prevention, recovery, and future token support fragile.

The original wording did not define the exact transaction boundary when one source event unlocks multiple achievements, how the daily cap is computed under concurrency, or how the economy rule version is retained. Those gaps are closed here.

## Decision

- Session state transitions are recorded as append-only `SessionEvent` records.
- The current session row is a checkpointed projection for fast access and recovery.
- Rewards use an append-only ledger with a unique idempotency key per source and currency.
- XP and future token balances are derived from the ledger.
- Session completion and its reward entry commit in one transaction.
- Task completion, achievement unlock, and their rewards also commit atomically.
- A cached total may exist for performance but must be rebuildable.
- Historical entries are not recalculated when rates change.
- Ledger entries are immutable through normal UI.

### Transaction boundaries

- **Session terminal transaction:** the transition to `Completed` or `Abandoned`, the session-XP ledger entry (including the daily-cap calculation), every achievement unlock and achievement-XP entry caused by that terminal state, the streak projection, and cached totals commit together.
- **Task completion transaction:** the first transition to `Done`, the 25 XP task entry, every achievement unlock caused by the task count, and cached totals commit together.
- **Achievement-only transaction:** when an achievement is satisfied by an event that does not itself change a session or task (for example, a streak threshold reached by a completion), the achievement unlock and its ledger entry commit with the source event's transaction.
- **Multiple achievements:** when one event satisfies several achievements, all unlocks and all ledger entries commit in one transaction, in ascending achievement-code order. A failure rolls back every unlock and entry; a retry may commit all of them once.
- **Daily cap:** the agent reads the sum of session-XP entries for the completion `AwardLocalDate` inside the same transaction and awards `min(computed, 300 - alreadyAwarded)`. No two concurrent requests can exceed 300.
- **Idempotency:** the unique key is checked inside the transaction. An existing key returns success-with-no-change. A duplicate achievement, task completion, or session terminal request creates no second row.
- **Economy rule version:** every ledger entry stores `EconomyRuleVersion = "ECONOMY-1"` for MVP. A session also stores `EconomyRuleVersion` for its award. This version identifies the economy rules that generated the amount; it does not version achievement eligibility criteria. A later version changes only new rewards.
- **Crash before commit:** no terminal state, no ledger entry, no achievement unlock, and no cache update. Recovery replays from the checkpoint.
- **Crash after commit:** the terminal state and ledger entry are present. Recovery finds the idempotency key and does not award again.
- **Uncertain retry:** the client retries with the same request and idempotency key; the agent returns the existing entry and does not create a second one.

### Conditions of acceptance

This ADR is accepted with conditions. The conditions are binding and must be verified in Phase 3 before implementation is considered validated:

1. Session completion, session XP, achievement unlock, streak projection, and cached totals are implemented as one transaction.
2. Task completion, task XP, achievement unlock, and cached totals are implemented as one transaction.
3. Multiple achievements from one event are atomic and deterministic.
4. The daily cap is computed inside the transaction and cannot be exceeded by retries or concurrency.
5. Every reward entry stores `ECONOMY-1`; historical entries are never recalculated.
6. Crash-before-commit, crash-after-commit, and uncertain-retry tests prove one award per source.
7. The ledger can rebuild total XP, level, daily session-XP totals, and achievement state.

## Consequences

- More schema and code complexity than a single mutable balance.
- Easier auditing, duplicate prevention, crash recovery, and future token support.
- Event payloads must be bounded and privacy-reviewed.
- The event log is not a substitute for periodic session checkpoints; it records meaningful transitions.
- Achievement persistence is atomic with the source event even when presentation is queued for a safe moment.
- A repair tool may be needed later, but it must be explicit and auditable.

## Alternatives considered

1. **Mutable XP counter only:** Rejected because duplicate detection and audit are weak.
2. **Ledger without session events:** Rejected because state recovery and violation/protection diagnostics are weaker.
3. **Event sourcing every timer tick:** Rejected as excessive write volume; checkpoints cover timing.
4. **Recompute all history on every rate change:** Rejected because historical rewards must remain explainable and stable.
5. **Defer achievement ledger entries until presentation:** Rejected because a crash before presentation could lose an earned award or allow a duplicate later.

## Review trigger

Review if event volume causes performance issues, if a future economy requires multi-entry transactions, or if a user-visible correction tool is added. Do not collapse to mutable balances without preserving idempotency.

## Phase 3 validation evidence (2026-11-12)

- `VERIFIED` at SQLite `3.49.1` level: a duplicate idempotency key raised `UNIQUE constraint failed` and left one ledger row and 25 XP.
- `VERIFIED`: a simulated failure rolled back both the reward row and the source-state update.
- `VERIFIED`: two concurrent writers with the same key produced exactly one commit and one duplicate error; one row and 35 total XP remained.
- `VERIFIED`: crash before commit left no uncommitted reward rows; crash after commit retained committed rows.
- `NOT TESTED`: session terminal transaction, task completion transaction, achievement fan-out, daily-cap calculation under concurrency, `ECONOMY-1` persistence, and total/level rebuild.
- Conditions 1–7 remain open. Database-level idempotency does not prove the domain transaction boundary.
- Status remains Accepted with conditions.
