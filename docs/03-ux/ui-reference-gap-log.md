# UI Reference Gap Log

**Product:** Boundless Study  
**Status:** Open  
**Last updated:** 2026-11-12  
**Visual approval:** None. No UI references or assets have been supplied.  
**Related:** `ux-specification.md`, `../README.md`

## 1. Purpose

This log records every missing visual reference or asset required to specify, implement, and approve Boundless Study’s UI. Functional requirements may proceed without these files. Visual implementation and visual approval may not.

### 1.1 Blocking boundary

Q-001 is a visual-only blocker. It blocks:

- Final visual styling.
- Reference-dependent visual implementation.
- Visual approval.
- Reference-conformance testing.

It does not block:

- Functional requirements or functional navigation.
- Technical validation.
- Business logic, database, focus engine, enforcement, progression, IPC, or Windows integration implementation.
- Temporary functional interfaces used to operate and test the product.
- Feature-level automated testing.

## 2. Current reference inventory

- UI reference images: **none supplied**.
- Icons: **none supplied**.
- Illustrations: **none supplied**.
- Logos/wordmarks: **none supplied**.
- Design tokens or style guide: **none supplied**.
- Fonts: **none supplied**.
- Animation or motion references: **none supplied**.
- Existing screen library or component library: **none supplied**.
- Asset manifest: **does not exist because there are no supplied assets**.

No colors, typography, spacing, iconography, illustration style, layout, or visual identity will be invented.

## 3. Status definitions

| Status | Meaning |
|---|---|
| `BLOCKED — AWAITING USER REFERENCES` | Functional behavior and a temporary functional interface may proceed; final visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing are not allowed. |
| `REFERENCE RECEIVED — PENDING INSPECTION` | File exists but has not been inspected and recorded. |
| `REFERENCE INSPECTED — PENDING APPROVAL` | File has been inspected, mapped, and proposed for use. |
| `VISUALLY APPROVED` | User has approved the screen against the supplied reference. |

Every row below is currently `BLOCKED — AWAITING USER REFERENCES`.

## 4. Missing references by screen and component

| ID | Screen or component | Required reference or asset | Why it is needed | Current functional specification status | Visual approval status |
|---|---|---|---|---|---|
| UIG-001 | Global visual system | Style guide or reference showing color, typography, spacing, radii, elevation, control states, and density. | Defines the visual language for every screen. | Not applicable; functional UX specified. | BLOCKED — AWAITING USER REFERENCES |
| UIG-002 | Global icon set | Supplied icon family and usage rules, including navigation, status, actions, and errors. | Prevents random icons, emoji, or inconsistent symbols. | Controls are functionally named; icons are unresolved. | BLOCKED — AWAITING USER REFERENCES |
| UIG-003 | Global illustration set | Supplied illustrations for onboarding, empty states, recovery, focus, and progress. | Defines illustration style and subject matter. | Asset purposes listed in UX §9. | BLOCKED — AWAITING USER REFERENCES |
| UIG-004 | App identity | App icon, wordmark/logo, tray variants, installer icon, notification icon. | Required for window, tray, installer, browser store, and toasts. | Functional use defined; visual files absent. | BLOCKED — AWAITING USER REFERENCES |
| UIG-005 | Window shell | Reference for window chrome, navigation shell, title area, resizing, minimum size, and system states. | Defines primary navigation and window behavior. | Navigation destinations defined in UX §3. | BLOCKED — AWAITING USER REFERENCES |
| UIG-006 | Focus and accessibility states | References for focus, hover, pressed, disabled, error, warning, high-contrast, and reduced-motion states. | Required for accessible and predictable controls. | Functional states defined; visual states absent. | BLOCKED — AWAITING USER REFERENCES |
| UIG-007 | Responsive and DPI behavior | References at supported window sizes, 100%–200% scaling, and mixed-DPI multi-monitor. | Prevents clipping and unintended layout changes. | NFR requires usability; visual behavior unresolved. | BLOCKED — AWAITING USER REFERENCES |
| UIG-008 | Onboarding: welcome and privacy (SCR-001) | Screen reference and privacy illustration if used. | Visual hierarchy for first-run trust and disclosure. | ONB-001; functional content defined. | BLOCKED — AWAITING USER REFERENCES |
| UIG-009 | Onboarding: environment check (SCR-002) | Screen and diagnostics-state reference. | Visual treatment of supported, unsupported, and policy-blocked states. | ONB-002; states defined. | BLOCKED — AWAITING USER REFERENCES |
| UIG-010 | Onboarding: mode comparison (SCR-003) | Reference for Full/Limited comparison and selection states. | Mode choice is a critical one-time decision. | ONB-003, ONB-004; capability matrix defined. | BLOCKED — AWAITING USER REFERENCES |
| UIG-011 | Onboarding: Full-mode permission (SCR-004) | Reference for elevation, service explanation, and install progress. | Must make privileged installation transparent. | ONB-005, SYS-002; behavior defined. | BLOCKED — AWAITING USER REFERENCES |
| UIG-012 | Onboarding: Limited-mode confirmation (SCR-005) | Reference for limited capability disclosure. | Must not look like a failed Full install. | ONB-006, SYS-003; content defined. | BLOCKED — AWAITING USER REFERENCES |
| UIG-013 | Onboarding: browser setup (SCR-006) | Reference for extension setup, connected, missing, and policy-blocked states. | Browser integration is a major capability boundary. | ONB-007, ONB-008, BRW-002, BRW-012. | BLOCKED — AWAITING USER REFERENCES |
| UIG-014 | Onboarding: first subject/task (SCR-007) | Screen and form-state reference. | First value moment and task creation. | ONB-009, STU-002. | BLOCKED — AWAITING USER REFERENCES |
| UIG-015 | Onboarding: first-session tutorial (SCR-008) | Reference for blocking, limits, recovery, and uninstall disclosure. | Must set honest expectations before enforcement. | ONB-010; required topics defined. | BLOCKED — AWAITING USER REFERENCES |
| UIG-016 | Today dashboard (SCR-009) | Desktop screen reference for all dashboard states. | Primary daily surface and status hierarchy. | DSH-001–007. | BLOCKED — AWAITING USER REFERENCES |
| UIG-017 | Focus setup (SCR-010) | Screen and control-state reference. | Profile/duration/task selection. | FOC-001, FOC-002. | BLOCKED — AWAITING USER REFERENCES |
| UIG-018 | Pre-session summary (SCR-011) | Reference for disclosure and start state. | Critical honesty surface before enforcement. | FOC-002, BRW-010, APP-010. | BLOCKED — AWAITING USER REFERENCES |
| UIG-019 | Active focus (SCR-012) | Reference for timer, status, task, and controls. | Main focus surface. | FOC-004–007, FOC-017. | BLOCKED — AWAITING USER REFERENCES |
| UIG-020 | Blocking overlay (SCR-013) | Reference for overlay on single and multiple monitors, with keyboard and screen-reader states. | Must interrupt without trapping. | APP-004–006, APP-011. | BLOCKED — AWAITING USER REFERENCES |
| UIG-021 | Break (SCR-014) | Screen and timer-state reference. | Separate rest flow. | FOC-008, NOT-002. | BLOCKED — AWAITING USER REFERENCES |
| UIG-022 | Session summary (SCR-015) | Reference for completed, early, abandoned, capped, and achievement states. | Main review and reward surface. | FOC-012, GAM-001–010, INS-005. | BLOCKED — AWAITING USER REFERENCES |
| UIG-023 | Session recovery (SCR-016) | Reference for interruption, checkpoint, resume, and abandon. | High-trust recovery moment. | FOC-011, SYS-019. | BLOCKED — AWAITING USER REFERENCES |
| UIG-024 | Plan and task list (SCR-017) | Desktop list, filter, empty, and archived-state reference. | Primary planning surface. | STU-001–012. | BLOCKED — AWAITING USER REFERENCES |
| UIG-025 | Task editor (SCR-018) | Form, validation, and destructive-action reference. | Core data entry. | STU-002–004. | BLOCKED — AWAITING USER REFERENCES |
| UIG-026 | Subjects (SCR-019) | List, archive, and empty-state reference. | Organization surface. | STU-001, STU-008. | BLOCKED — AWAITING USER REFERENCES |
| UIG-027 | Progress and insights (SCR-020) | Charts, tables, filters, and empty-state reference. | Visual data representation and accessibility. | INS-001–010. | BLOCKED — AWAITING USER REFERENCES |
| UIG-028 | Session history (SCR-021) | List/table and detail reference. | Review and audit surface. | INS-004, FOC-018. | BLOCKED — AWAITING USER REFERENCES |
| UIG-029 | Achievements (SCR-022) | Achievement badges and level/streak reference. | Visual reward identity. | GAM-003–005, GAM-010. | BLOCKED — AWAITING USER REFERENCES |
| UIG-030 | Settings home (SCR-023) | Settings shell and navigation reference. | Configuration organization. | SET-001–016. | BLOCKED — AWAITING USER REFERENCES |
| UIG-031 | Focus profiles and defaults (SCR-024) | Profile selector and duration reference. | Profile behavior and defaults. | SET-001, FOC-003. | BLOCKED — AWAITING USER REFERENCES |
| UIG-032 | Blocked applications (SCR-025) | App picker, list, and exception-state reference. | Application enforcement configuration. | APP-001–003. | BLOCKED — AWAITING USER REFERENCES |
| UIG-033 | Blocked and allowed sites (SCR-026) | Domain list, validation, and precedence reference. | Browser enforcement configuration. | BRW-006–007, SET-002–003. | BLOCKED — AWAITING USER REFERENCES |
| UIG-034 | Notifications (SCR-027) | Preference and quiet-hours reference. | Notification control. | NOT-006–008, SET-004. | BLOCKED — AWAITING USER REFERENCES |
| UIG-035 | Data and backup (SCR-028) | Backup, restore, export, integrity, and delete reference. | High-risk data operations. | DAT-004–012. | BLOCKED — AWAITING USER REFERENCES |
| UIG-036 | Installation mode and upgrade (SCR-029) | Limited/Full status and upgrade reference. | Critical mode transition. | SYS-020–023, SET-010. | BLOCKED — AWAITING USER REFERENCES |
| UIG-037 | Diagnostics (SCR-030) | Health, versions, connection test, and export reference. | Supportability. | SYS-022–026, SET-008. | BLOCKED — AWAITING USER REFERENCES |
| UIG-038 | Recovery and uninstall guide (SCR-031) | Reference for recovery, component removal, data choice, and extension removal. | Trust and reversibility. | ONB-010, DAT-011, SYS-024. | BLOCKED — AWAITING USER REFERENCES |
| UIG-039 | About and privacy (SCR-032) | Reference for version, privacy, licenses, and support. | Transparency. | SET-009. | BLOCKED — AWAITING USER REFERENCES |
| UIG-040 | Browser connection test (SCR-033) | Reference for per-surface status. | Browser integration diagnosis. | BRW-018, SET-015. | BLOCKED — AWAITING USER REFERENCES |
| UIG-041 | Error and degraded states (SCR-034) | Reference for each error class, blocked-by-policy, service degraded, agent unavailable, database recovery, and upgrade failure. | Prevents improvised error UI. | XCT-007, SYS-022. | BLOCKED — AWAITING USER REFERENCES |
| UIG-042 | Tray menu (SCR-035) | Tray icon states and menu reference. | Background presence and active session status. | FOC-017, SYS-006. | BLOCKED — AWAITING USER REFERENCES |
| UIG-043 | Browser extension blocked page | Extension-hosted explanation page reference. | User sees why a site is blocked. | BRW-015. | BLOCKED — AWAITING USER REFERENCES |
| UIG-044 | Browser extension status surface | Extension popup or status page reference, if included in product scope. | Connection and protection status. | BRW-004, BRW-018. | BLOCKED — AWAITING USER REFERENCES |
| UIG-045 | Windows notifications | Toast identity, icon, text hierarchy, and action reference. | Session and protection alerts. | NOT-001–009. | BLOCKED — AWAITING USER REFERENCES |
| UIG-046 | Installer and uninstaller | Full/Limited installer screens, progress, failure, repair, and uninstall data choice. | First trust surface and clean removal. | SYS-001–003, SYS-024. | BLOCKED — AWAITING USER REFERENCES |
| UIG-047 | Store listing | Chrome Web Store and Edge Add-ons listing assets and privacy visuals. | Browser distribution. | BRW-019. | BLOCKED — AWAITING USER REFERENCES |
| UIG-048 | Dark or alternate appearance | Reference only if the product is intended to support an alternate appearance. | Prevents invented themes. | Not scoped until references. | BLOCKED — AWAITING USER REFERENCES |
| UIG-049 | Motion and reduced motion | Motion reference and reduced-motion behavior. | Prevents invented animation. | Not scoped until references. | BLOCKED — AWAITING USER REFERENCES |
| UIG-050 | Content and copy tone | Approved voice, capitalization, terminology, and microcopy samples. | Keeps UI copy consistent with the user’s product voice. | Functional terminology defined in UX §2. | BLOCKED — AWAITING USER REFERENCES |

## 5. Reference intake checklist

When references are supplied:

1. Record original file name, path, format, dimensions, and intended screen.
2. Preserve originals unchanged.
3. Inspect every reference before specifying that screen.
4. Map each asset to one or more screen IDs.
5. Record required states: default, hover, focus, pressed, disabled, loading, empty, error, warning, active, and high contrast where applicable.
6. Record light/dark or alternate variants only if supplied.
7. Verify aspect ratios and required export sizes.
8. Update the central asset manifest.
9. Update `ux-specification.md` and this log.
10. Obtain explicit visual approval before claiming visual implementation matches a reference.
11. If references remain unavailable when the dedicated UI phase begins, stop and obtain the user's direction before inventing a visual design.

## 6. Asset manifest requirement

The asset manifest will be created only after files are supplied. Minimum fields:

- Asset ID.
- Original file and stored path.
- Format and dimensions.
- Intended screen/component.
- Required variants/states.
- Aspect ratio constraint.
- Color/background dependencies, if any.
- Alt text or decorative designation.
- Source and approval status.
- Do not stretch, recolor, or substitute without approval.

## 7. Prohibited substitutions

- No emoji as product icons.
- No randomly selected icon packs.
- No generated illustrations presented as supplied assets.
- No stretching or aspect-ratio distortion.
- No borrowed visual identity from gstack, unslop, or another archive.
- No theme or color selection made without references.
- No claim that a screen is visually approved when only a functional layout exists.

## 8. Functional status

Functional UX and functionality-first implementation are not blocked by this log. Flows, screen purposes, content hierarchy, controls, states, validation, accessibility behavior, and acceptance criteria are specified in `ux-specification.md`. A basic functional interface may be built and tested. Visual styling, reference-dependent visual implementation, visual approval, and reference-conformance testing remain blocked for every row.

If references are still unavailable when the dedicated UI phase begins, stop and obtain the user's direction before inventing a visual design.
