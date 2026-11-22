# ADR-0008: Supplied visual references are authoritative

- **Status:** Accepted
- **Date:** 2026-11-12
- **Decision owner:** User
- **Related:** UX specification, UI reference gap log

## Context

The user will supply UI reference images, icons, illustrations, and other visual assets. The project must not invent a new visual identity, substitute random icons or emoji, stretch images, or claim visual approval without evidence. The project folder currently contains no references.

## Decision

- Supplied references and assets are authoritative for layout, hierarchy, styling, and asset use.
- Functional UX can proceed without visual references.
- Every missing visual reference is recorded in `03-ux/ui-reference-gap-log.md` with status `BLOCKED — AWAITING USER REFERENCES`.
- Visual implementation is blocked until the relevant reference is supplied and inspected.
- No colors, typography, spacing, layout styling, themes, icons, illustrations, or generated replacement assets are chosen without approval.
- Supplied assets retain their aspect ratio and are not stretched.
- A central asset manifest will be created from supplied files, not invented entries.
- gstack or any other archive’s website appearance is not a design system for Boundless Study.
- `BLOCKED` means blocked for visual implementation, not blocked for functional requirements.

## Consequences

- Screens can have functional specifications, information hierarchy, controls, states, and acceptance criteria without visual approval.
- Visual QA cannot start until references arrive.
- Asset storage, naming, resolution, and format requirements must be defined when references are provided.
- Screen implementation may be sequenced around available references.

## Alternatives considered

1. **Use a generic design system now:** Rejected because it would invent a visual identity and conflict with user-supplied references.
2. **Generate placeholder illustrations:** Rejected as a substitute for authoritative assets.
3. **Use another product’s visual style:** Rejected.
4. **Wait for all references before any UX work:** Rejected because functional flows and screen purposes are independent of visual styling.

## Review trigger

Review when the user supplies references. Update the gap log and UX specification before any visual implementation.
