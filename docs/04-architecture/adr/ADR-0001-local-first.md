# ADR-0001: Local-first architecture with no mandatory cloud

- **Status:** Accepted
- **Date:** 2026-11-12
- **Decision owner:** User
- **Related:** Product requirements, data model, NFR privacy

## Context

Boundless Study targets students who need a dependable study tool without mandatory accounts, subscriptions, or cloud connectivity. Study data is sensitive because it reveals habits, subjects, and difficulties. The product must survive offline use and remain useful on school-managed devices.

## Decision

- Core functionality operates without an account or network connection.
- The system of record is a local SQLite database under the user’s profile.
- No telemetry or study data is transmitted by default.
- Backups and exports are local files controlled by the user.
- Any future network feature is optional, explicitly consented to, and separately scoped.
- Browser extension distribution and updates are external dependencies, not a reason to make the core cloud-dependent.

## Consequences

- No server-side account recovery, cross-device sync, or cloud backup in MVP.
- Local data can be read or modified by the local owner or administrator; this limitation is disclosed.
- Support relies on user-initiated diagnostics bundles.
- The browser extension store remains an external availability dependency.
- The data model and backup format must be durable without a remote service.

## Alternatives considered

1. **Mandatory cloud account:** Rejected for privacy, offline use, and unnecessary complexity.
2. **Optional cloud sync in MVP:** Deferred; it would expand security, identity, conflict resolution, and support scope.
3. **Local database plus anonymous telemetry:** Rejected by default; telemetry would need explicit consent and a separate review.

## Review trigger

Revisit only if the user approves a separate cloud or sync product scope. Local-first remains the default for the MVP.
