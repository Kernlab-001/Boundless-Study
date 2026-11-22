# BPE-1.3 Durable Simulation Architecture

## Scope

BPE-1.3 extends the isolated `prototypes/Boundless.Protection.Engine` simulator with durable SQLite state and real child-process crash tests. It does not implement Windows enforcement, a privileged broker, or production integration. AppLocker-shaped and WFP-shaped providers only model policy data in synthetic databases.

## Stores

- **Protection journal:** `SqliteProtectionJournal` stores versioned operation records, request/session/generation/owner identifiers, state/failure, fingerprints, baseline version/fingerprint, and serialized intended/applied rules. Startup checks SQLite integrity and schema version. It rejects malformed or semantically invalid records and leaves the stored bytes untouched.
- **Synthetic policy store:** `PersistentSimulatedPolicyProvider` stores rules, owner IDs and policy version in its own SQLite database. Mutations use immediate transactions. Application compares current version and fingerprint to the baseline. Restore first validates every candidate and removes only exact owner/content matches; it never writes a baseline snapshot back over current state.
- **Audit store:** `SqliteProtectionAuditSink` appends timestamped audit records. Events contain operation IDs, enums, and rule counts, not SIDs, paths, hashes, credentials, or user content. Audit storage is not tamper-proof.

The journal, audit, and policy stores are intended to use separate synthetic database files. Journal records and provider rows retain identity fields needed for exact recovery and are plaintext at rest; this prototype does not claim encryption.

## Durability and recovery

Each store uses WAL, `synchronous=FULL`, and a bounded SQLite busy timeout. Database schema versions are checked at open. Process-crash tests execute a worker process against temporary files, terminate it with `Environment.FailFast`, then reopen new journal/provider objects and call recovery. This proves process-level persistence for the exercised simulated boundaries on the test host; it does not prove recovery against a real policy store or hardware/OS failures.

## Cross-process coordination

The durable journal exposes an operation lock backed by a sibling file-region lock. It polls with cancellation and a five-second acquisition bound; process termination closes the handle and releases the lock. The guarantee is limited to cooperating processes using the same journal path on a local Windows or Linux filesystem. macOS and network filesystems are unsupported. SQLite transactions separately serialize individual store mutations; the operation lock spans the engine's journal/provider sequence.

## Integration boundary

Engine, test, and crash-worker projects appear under `/prototypes/` in `BoundlessStudy.slnx`. Production projects do not reference them. All BPE data tests use disposable temporary directories and synthetic identities. No host policies, firewall configuration, production database, service, process-control API, or actual Windows policy API is used.
