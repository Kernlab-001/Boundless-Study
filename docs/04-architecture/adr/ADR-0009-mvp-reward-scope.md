# ADR-0009: MVP progression is XP, levels, streak, and achievements only

- **Status:** Accepted
- **Date:** 2026-11-12
- **Decision owner:** User
- **Related:** Product requirements, domain rules, gamification

## Context

The full product vision includes XP, levels, tokens, achievements, challenges, a reward shop, themes, and a companion. The approved core-loop MVP includes basic rewards but defers the token shop, challenges, companion, and themes. Delivering a token currency with no meaningful sink would confuse users and expand economy balance work prematurely.

At the same time, future tokens and mode upgrades must preserve progression data.

## Decision

- MVP exposes XP, levels, streaks, and five achievements.
- MVP does not expose token earning, a token balance, token spending, a shop, themes, or companion progression.
- The reward ledger schema supports a `TOKEN` currency from the start.
- No token entries are created in MVP.
- Future token entries, if present in a later build, survive migrations, backups, restores, and Full/Limited upgrades.
- XP and token balances are derived from the ledger.
- No rewards are granted for app launch or passive presence.

## Consequences

- The MVP has a simpler economy and fewer art dependencies.
- Achievement names and criteria are functional copy; badge art remains blocked.
- Future token economy can attach to existing events without replacing the ledger.
- Users may see XP and achievements but no spendable currency in MVP.
- The roadmap must explicitly introduce token sources and sinks together, not tokens alone.

## Alternatives considered

1. **Include tokens and shop in MVP:** Rejected due to scope, art, and economy balance.
2. **Omit token schema entirely:** Rejected because it would increase upgrade and data-preservation risk.
3. **Use XP as a spendable currency:** Rejected because it would complicate levels and reward integrity.

## Review trigger

Review when the Motivation Expansion release is scoped. Token sources, sinks, prices, and preservation tests must be approved before implementation.
