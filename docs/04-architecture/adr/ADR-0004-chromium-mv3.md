# ADR-0004: Chrome/Edge Manifest V3 with declarativeNetRequest

- **Status:** Proposed
- **Date:** 2026-11-12
- **Decision owner:** User approval required
- **Related:** Browser Guardian, system architecture, privacy NFR

## Context

Website restrictions in MVP need to block configured domains during a focus session while allowing explicit educational domains. The product is Windows-first and should avoid system-wide network interception, a proxy, or a kernel driver.

Chrome and Edge both support Chromium Manifest V3 and Native Messaging. Other Chromium browsers may work but would expand the test and store matrix. Incognito, browser-internal pages, extension-disabled states, and school policy remain limitations.

## Decision

- MVP supports Chrome stable and Edge stable.
- Use a Manifest V3 extension in TypeScript.
- Use `declarativeNetRequest` dynamic rules for blocked and allowed domains.
- Use Native Messaging for policy, heartbeat, and status.
- Request only permissions needed for declarative blocking, storage, alarms, and Native Messaging.
- Do not request browsing history, cookies, bookmarks, page content, or form data.
- Fixed extension ID and allowlisted Native Messaging origin.
- Explicit allow rules take precedence over blocked domains.
- Rules carry a session ID and expiry; the extension clears them on session end or expiry.
- The product never silently installs the extension.

## Consequences

- Website protection requires the extension, bridge, and agent to be healthy.
- Incognito and other browsers are not covered by default and must be disclosed.
- Chrome Web Store and Edge Add-ons review become release dependencies.
- The extension is subject to store policy and browser management restrictions.
- Strict with website rules cannot start unless protection is verified.
- Limited mode can still use website rules when policy permits.
- DNR rule limits and matching semantics must be tested before implementation.

## Alternatives considered

1. **System proxy or VPN:** Rejected as invasive, broad, and difficult to uninstall transparently.
2. **Hosts-file modification:** Rejected as system-wide, admin-dependent, and brittle.
3. **DNS filtering:** Rejected for MVP; bypassable and impacts other applications.
4. **Content-script page blocking:** Rejected as weaker and more privacy-invasive than declarative rules.
5. **Firefox support:** Deferred; different extension APIs and store path.

## Review trigger

Review after Phase 3 extension proof and store-policy review. If DNR cannot meet the rule model within browser limits, revise the browser scope rather than claim unsupported protection.

## Phase 3 validation evidence (2026-11-12)

- `VERIFIED` in Chrome 157 and Edge 157: Manifest V3 service worker loaded; the fixed manifest-key ID was `jjfgoeghdckpppcfjinhcccffmkmkibn` in both browsers.
- `VERIFIED`: a dynamic DNR block rule for `||example.com^` was created and `testMatchOutcome` matched rule 1001; a session rule for `||session.example^` was created.
- `VERIFIED` in Edge: after a browser restart, the dynamic rule persisted, the session rule was cleared, and Native Messaging reconnected.
- `DOCUMENTED`: Chromium documents dynamic-rule persistence and session-rule clearing. The Chrome unpacked harness reloads the extension on each launch, so Chrome restart persistence was not proven locally.
- `VERIFIED`: HKCU Native Messaging registration launched a compiled host and returned a framed pong in both browsers.
- `BLOCKED`: store-assigned extension ID, store review, policy push, heartbeat, focus-session synchronization, extension-disabled detection, and policy-failure states were not tested.
- Disposition: evidence supports acceptance with conditions. Status remains Proposed pending user approval and store/integration evidence.

### Closure-round evidence (2026-11-13)

- `DOCUMENTED` from official vendor documentation: the Chrome Web Store officially supports keeping the development extension ID in production (upload without publishing → view public key → embed in manifest `key` → ID matches the dashboard Item ID). Edge Add-ons publishing requires a Partner Center account (MSA), has no registration fee, and offers individual/company accounts. Native Messaging host registration on Windows is documented for HKCU (per-user) and HKLM (whole-machine).
- The final store-assigned ID confirmation, store review, and account creation remain external with owner/prerequisites/completion conditions recorded in `07-phase-3/phase-3-validation-record.md` §3.12.
- Status remains Proposed pending user approval.
