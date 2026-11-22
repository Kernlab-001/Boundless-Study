# ADR-0005: SQLite with EF Core and a single-writer agent

- **Status:** Accepted with conditions
- **Date:** 2026-11-12
- **Decision owner:** User approval required for final acceptance
- **Related:** Data model, reliability NFR, system architecture, ADR-0006, ADR-0007

## Context

Boundless Study needs a local, dependable, queryable store for tasks, sessions, events, rewards, and settings. The target stack specifies SQLite and Entity Framework Core. Both the WPF UI and background components need data, but multiple writers create locking, migration, and recovery risks.

The original wording said that only the agent writes. That left the read path ambiguous: a UI process could open the database directly for reads, creating a second access boundary, a second set of schema assumptions, and a way to bypass agent timeouts, validation, and backup coordination.

## Decision

- Use SQLite as the local system of record.
- Use Entity Framework Core migrations.
- The per-user agent is the **sole database owner** and the **only process that opens the database**, for reads and writes.
- The UI, Windows service, native bridge, and browser extension never open the database. They access data only through documented agent IPC.
- The UI sends queries and commands through the user-scoped UI pipe.
- The bridge sends browser status and receives policy through the bridge pipe. It stores no study data.
- The service uses only the control pipe for health and supervision. It has no database access.
- Use WAL mode, foreign keys, bounded busy retries, and transactional domain operations.
- Run migrations in the agent before serving UI commands.
- Create a pre-migration backup when data may be altered.
- Use a consistent online backup mechanism rather than copying an open database file.
- Keep the data path and schema identical in Full and Limited modes.

### Read path

- A read request contains `protocolVersion`, `requestId`, `correlationId`, `queryType`, bounded parameters, `pageSize`, an optional opaque `pageToken`, and an optional `snapshotToken`.
- The agent validates the request, then executes the query inside a read transaction. A single response is a consistent WAL snapshot.
- Multi-page results use keyset pagination with an opaque `pageToken` and a bounded `snapshotToken` lease. If the lease expires, the agent returns `SNAPSHOT_EXPIRED` and the UI restarts the query rather than mixing snapshots silently.
- Page size is bounded. The default is 100 records; the maximum is 500.
- The response contains the echoed `requestId`, `correlationId`, `snapshotToken`, page records, `hasMore`, and a stable sort key.
- The default request timeout is 5 seconds. Long-running queries report progress and can be cancelled.
- A cancellation request names the target `requestId`; the agent checks cancellation between pages and before returning a response.
- Errors return a stable code, a retryable flag, and no partial mutation. A timeout does not imply that a write failed; the client must reconcile by request or idempotency key.

### Write path

- A write request contains `protocolVersion`, `requestId`, `correlationId`, `commandType`, validated parameters, and an idempotency key for mutations.
- The agent serializes writes. A command either commits completely or leaves no partial mutation.
- A duplicate command with the same idempotency key returns the original result as success-with-no-change.
- The UI never retries a write by opening the database, editing a file, or bypassing the agent.

### Backup and migration ownership

- The agent coordinates backup and restore. Backup uses SQLite's online backup mechanism or an equivalent consistent snapshot while the agent controls writes.
- The UI requests a backup or restore and receives progress and the final outcome. It does not copy the database file.
- The agent runs migrations before serving commands, records the schema version, and checks it after restore.
- The UI performs a schema-version handshake with the agent. It never migrates the database.
- If the agent is unavailable, the UI shows a start/repair/diagnostics state. It does not open the database directly.

### Conditions of acceptance

This ADR is accepted with conditions. The conditions are binding and must be verified in Phase 3 before implementation is considered validated:

1. Code and integration tests prove that the UI, service, bridge, and extension never open the SQLite file.
2. The IPC contract implements request IDs, correlation IDs, timeouts, cancellation, bounded pagination, and snapshot-expiry behavior.
3. WAL read concurrency, snapshot leases, and writer starvation are tested at the reference data volume.
4. Backup and restore run only through the agent, including the pre-migration and pre-restore safety backups.
5. Migration ownership and schema-version handshake tests pass in both Full and Limited modes.
6. Agent unavailability produces a recovery state, never a direct-database fallback.

## Consequences

- The agent becomes a required local service for both modes; it is not the privileged Windows service.
- Queries and commands need a versioned IPC contract with pagination, timeouts, cancellation, and snapshot semantics.
- The UI cannot function without the agent and must offer recovery, not direct database fallback.
- Single-writer design simplifies reward idempotency and daily-cap transactions.
- Read concurrency must be validated; a long-lived snapshot lease can delay WAL checkpointing and must be bounded.
- Performance must be validated at the stated data volume.
- Database encryption is not provided in MVP; local access limitations are disclosed.

## Alternatives considered

1. **UI owns the database:** Rejected because closing the UI would stop tracking and because background enforcement also needs state.
2. **UI reads the database directly while the agent writes:** Rejected because it creates a second access boundary, bypasses agent validation and timeouts, and complicates backup, migration, and schema compatibility.
3. **Service owns the database:** Rejected because it runs as a privileged account in Session 0 and complicates per-user data, ACLs, and multi-user support.
4. **Multiple writers with retries:** Rejected as unnecessary complexity and a higher corruption/recovery risk.
5. **LiteDB or JSON files:** Rejected for query, transaction, migration, and integrity limitations.
6. **Cloud database:** Rejected by local-first scope.

## Review trigger

Revisit if the agent process is removed, if multiple concurrent writer processes become necessary, or if measured performance fails the NFR targets. Any change must preserve reward atomicity, the single access boundary, and data ownership.

## Phase 3 validation evidence (2026-11-12)

- `VERIFIED` with SQLite `3.49.1`: WAL mode, a reader during an open write saw only 1 committed row, 101 after commit, and a stable snapshot (101/101/126).
- `VERIFIED`: a zero-timeout writer failed with `database is locked`; a bounded waiter succeeded after 0.657 s.
- `VERIFIED`: online backup completed with `integrity_check=ok` and 240 rows during concurrent writes.
- `VERIFIED`: crash before commit rolled back 500 uncommitted rows; crash after commit retained 500 committed rows plus marker; integrity remained `ok`; WAL truncated to 0 bytes.
- `BLOCKED` (first round): EF Core 10 migrations, `Microsoft.Data.Sqlite`, schema-version handshake, pre-migration backups, restore, and packaged-runtime behavior were not tested because no .NET SDK is installed. Superseded by the closure round below.
- Conditions 1–6 remain open. Engine-level SQLite evidence does not prove EF Core integration or the agent-only access boundary.
- Status remains Accepted with conditions.

### Closure-round evidence (2026-11-13, EF Core 10)

- `VERIFIED` with .NET 10 SDK 10.0.404 (per-user install), EF Core 10 and `Microsoft.Data.Sqlite`: database creation, initial migration, subsequent migration, `__EFMigrationsHistory` schema-version handshake, tamper detection (MISMATCH on a removed history row), transactional ledger+event commit and rollback, duplicate idempotency-key rejection, WAL snapshot isolation through EF, `SqliteConnection.BackupDatabase` plus simulated restore with `integrity_check=ok`, WAL checkpoint truncate, migration rollback after a mid-migration process kill (integrity ok, history unchanged, data preserved; the recovery re-run blocked indefinitely on a stale committed `__EFMigrationsLock` row — see new condition below — and completed in 3.2 s with data preserved after the stale lock was manually cleared), and a v1→v2 schema upgrade preserving data.
- `VERIFIED` (honest boundary): SQLite permits extra connections; single-owner enforcement is architectural, exactly as this ADR states. A second connection opened in the spike.
- New condition validated by the spike: a compiled model newer than the applied schema must not access the database (v2 entity insert into a v1 table failed). The schema-version handshake must gate all access before first query.
- **New crash-safety condition (R-P3-16):** the migration lock is not crash-safe — a process killed mid-migration leaves its committed `__EFMigrationsLock` row, and the next migration attempt blocks indefinitely. Product migration code must detect and clear a stale lock before migrating.
- Conditions on EF Core integration are now validated at spike level; product adoption of the handshake and stale-lock-clearing patterns remains. Status remains Accepted with conditions.
