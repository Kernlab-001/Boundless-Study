# Boundless Study — BPE-1.7 Implementation Report

**Milestone:** BPE-1.7 — Windows OS Identity Boundary Integration & End-to-End Authorized Policy Evaluation  
**Target Scope:** `prototypes/Boundless.Protection.Engine`, `prototypes/Boundless.Protection.Engine.Tests`  
**Enforcement Status:** Simulation-only policy decision reporting (`PolicyEvaluationResult.IsSimulation == true`, `CanClaimProtection == false`); OS identity, process, and executable verification are real Windows Win32 checks, while no OS policy enforcement (AppLocker/WFP/firewall/ACL) is performed.

---

## 1. Architecture Overview

BPE-1.7 closes the authorization boundary gap deferred from BPE-1.6.1 by wiring the three Windows OS-backed verifiers into a unified `IIdentityBoundaryVerifier` implementation and connecting it to `ProtectionPolicyEvaluator.EvaluateAuthorizedAsync`:

```text
CallerSessionIdentityClaims + CallerProcessIdentityClaims + PolicyTarget + evaluatedAtUtc + rules
  │
  ▼
ProtectionPolicyEvaluator.EvaluateAuthorizedAsync (public)
  ├─ [!_isWindows()] ──► Closed(PolicyEvaluationStatus.IdentityUnsupported,
  │                             "platform-unsupported-windows-identity-required")
  └─ [_isWindows()]  ──► verifier = _identityVerifier ?? WindowsIdentityBoundaryVerifier.Default
        │
        ├─ [verifier.SourceTrust != OperatingSystemVerified]
        │     ──► Closed(IdentityUnsupported, "verifier-source-not-trusted")
        │
        ├─ 1. verifier.VerifySessionAsync(sessionClaims, ct)
        │     └─► WindowsSessionIdentityVerifier (Advapi32 OpenProcessToken + TokenUser SID + TokenStatistics LUID + TokenSessionId)
        │         └─► IdentityTrustBoundary.IsTrustedSession + SessionEvidenceMatchesClaims
        │
        ├─ 2. verifier.VerifyProcessAsync(processClaims, verifiedSession, ct)
        │     ├─► WindowsProcessIdentityVerifier (Kernel32 OpenProcess + GetProcessTimes + GetExitCodeProcess +
        │     │                                   WaitForSingleObject + OpenProcessToken SID/SessionId +
        │     │                                   ProcessIdToSessionId + same-handle re-verification)
        │     └─► WindowsExecutableIdentityVerifier.VerifyAsync(claims.ExecutablePath, claims.ExecutableSha256, ct)
        │                                        (Kernel32 CreateFileW + GetFileInformationByHandle/Ex +
        │                                         GetFinalPathNameByHandleW + SHA256.HashDataAsync +
        │                                         post-hash canonical path re-verification)
        │         └─► IdentityTrustBoundary.IsTrustedProcess + ProcessEvidenceMatchesClaims
        │
        └─ 3. ProtectionPolicyEvaluator.Evaluate(verifiedRequest, rules)
              └─► ProtectedTargetCatalog exclusion -> Rule precedence (Exception > Deny > Allow > Default Restrict)
                  └─► PolicyEvaluationResult (IsSimulation = true, CanClaimProtection = false)
```

---

## 2. Components Implemented & Modified

### 2.1 `WindowsIdentityBoundaryVerifier` (New)
- **File:** `prototypes/Boundless.Protection.Engine/WindowsIdentityBoundaryVerifier.cs`
- **Contract & Trust Source:**
  - `public sealed class WindowsIdentityBoundaryVerifier : IIdentityBoundaryVerifier`
  - `SourceTrust => IdentityEvidenceSourceTrust.OperatingSystemVerified`
  - `public static WindowsIdentityBoundaryVerifier Default => new WindowsIdentityBoundaryVerifier();`
  - Exposes a single public parameterless constructor (matching `WindowsSessionIdentityVerifier`, `WindowsProcessIdentityVerifier`, and `WindowsExecutableIdentityVerifier`) and an `internal` constructor accepting custom verifier instances and `Func<bool>? isWindows` for deterministic platform/unit testing.
- **Session Verification (`VerifySessionAsync`):**
  - Enforces null and cancellation guards (`ArgumentNullException.ThrowIfNull`, `cancellationToken.ThrowIfCancellationRequested`).
  - Fails closed on non-Windows (`!_isWindows()`) with `IdentityVerificationStatus.Unsupported` (`"platform-unsupported-windows-identity-required"`).
  - Delegates to `WindowsSessionIdentityVerifier.VerifySessionAsync(claims, cancellationToken)`.
- **Process & Executable Verification (`VerifyProcessAsync`):**
  - Enforces null, cancellation, and `_isWindows()` platform guards.
  - Step 1: Calls `WindowsProcessIdentityVerifier.VerifyProcessAsync(claims, session, cancellationToken)`. On failure (or missing/untrusted evidence), fails closed with the verifier's exact `Status` and `ReasonCode`.
  - Step 2: Calls `WindowsExecutableIdentityVerifier.VerifyAsync(claims.ExecutablePath, claims.ExecutableSha256, cancellationToken)`. On failure, maps `Unavailable` / `Unsupported` / `Rejected` to the corresponding `IdentityVerificationResult<VerifiedProcessIdentity>` failure preserving `ReasonCode`.
  - Step 3: On success of both checks, returns `IdentityVerificationResult<VerifiedProcessIdentity>.Verified(...)` combining the OS-verified PID, kernel start instant, token owner SID, session ID/generation, canonical DOS executable path (`GetFinalPathNameByHandleW`), OS-computed uppercase SHA-256 hex digest, and UTC observation timestamp (`max(process.ObservedAtUtc, executable.ObservedAtUtc)` clamped >= `ProcessStartUtc`).

### 2.2 `WindowsExecutableIdentityVerifier` (Updated)
- **File:** `prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs`
- Added the two-argument async overload:
  ```csharp
  public Task<IdentityVerificationResult<VerifiedExecutableIdentity>> VerifyAsync(
      string claimedPath,
      string claimedSha256,
      CancellationToken cancellationToken = default) =>
      VerifyAsync(claimedPath, claimedPath, claimedSha256, cancellationToken);
  ```
- Added an `internal WindowsExecutableIdentityVerifier(Func<bool> isWindows, Func<DateTimeOffset> utcNow)` constructor (preserving the single `public WindowsExecutableIdentityVerifier()` constructor) and routed platform/clock calls through `_isWindows` and `_utcNow` consistently with the session and process verifiers.

### 2.3 `ProtectionPolicyEvaluator` (Updated)
- **File:** `prototypes/Boundless.Protection.Engine/PolicyEvaluation.cs`
- **Constructor Injection for Test Harnesses:**
  - Kept `public ProtectionPolicyEvaluator(ProtectedTargetCatalog protectedTargets, TimeSpan? maximumIdentityAge = null)` unchanged for public callers.
  - Added `internal ProtectionPolicyEvaluator(ProtectedTargetCatalog protectedTargets, TimeSpan? maximumIdentityAge, IIdentityBoundaryVerifier? identityVerifier, Func<bool>? isWindows = null)` allowing in-assembly test harnesses (`InternalsVisibleTo`) to supply an `IIdentityBoundaryVerifier` and/or platform predicate without exposing `IIdentityBoundaryVerifier` on the public API.
- **Public `EvaluateAuthorizedAsync` Wiring:**
  - Checks `cancellationToken.ThrowIfCancellationRequested()`, `ArgumentNullException.ThrowIfNull(sessionClaims)`, `ArgumentNullException.ThrowIfNull(processClaims)`.
  - On non-Windows (`!_isWindows()`), returns `Closed(PolicyEvaluationStatus.IdentityUnsupported, "platform-unsupported-windows-identity-required")`.
  - On Windows (`_isWindows()`), resolves `var verifier = _identityVerifier ?? WindowsIdentityBoundaryVerifier.Default;` and delegates to `internal EvaluateAuthorizedAsync(verifier, ...)`.
- **Live Verification Timestamp Reconciliation (`ResolveLiveVerificationTimestamp`):**
  - When a caller invokes `EvaluateAuthorizedAsync(..., DateTimeOffset.UtcNow, ...)` on Windows, live Win32 token inspection and single-handle SHA-256 hashing take milliseconds after `evaluatedAtUtc` was captured at the call site, causing `AuthenticatedAtUtc` / `ObservedAtUtc` to be slightly later than `evaluatedAtUtc`.
  - `ResolveLiveVerificationTimestamp` advances the effective evaluation timestamp to `evidenceObservedAtUtc` **only** when:
    1. `verifier is WindowsIdentityBoundaryVerifier`,
    2. `evaluatedAtUtc.Offset == TimeSpan.Zero`,
    3. `evaluatedAtUtc` was captured within 250 ms prior to verification start and is `<= verificationNowUtc`, and
    4. the evidence timestamp was minted during that exact verification window (`[verificationStartedUtc, verificationNowUtc]`) and precedes `session.ExpiresAtUtc`.
  - Historical, non-UTC, expired, or stale timestamps continue to fail closed without adjustment.
- **Simulation Isolation Preserved:**
  - `PolicyEvaluationResult.IsSimulation` remains `true` and `CanClaimProtection` remains `false`.
  - Any verifier whose `SourceTrust != IdentityEvidenceSourceTrust.OperatingSystemVerified` (including `SimulatedUntrusted` and `CallerSupplied`) is rejected immediately before invoking verification methods (`IdentityUnsupported`, `"verifier-source-not-trusted"`).

### 2.4 `IdentityTrustBoundary` (Documentation Update)
- **File:** `prototypes/Boundless.Protection.Engine/IdentityTrustBoundary.cs`
- Updated the `IIdentityBoundaryVerifier` XML doc comment to reflect that `WindowsIdentityBoundaryVerifier` implements the interface and is wired into `ProtectionPolicyEvaluator.EvaluateAuthorizedAsync`.

### 2.5 Test Suite Updates & New E2E Tests
- **New:** `prototypes/Boundless.Protection.Engine.Tests/WindowsAuthorizedEvaluationTests.cs` (16 test cases covering real Windows session/process/executable end-to-end Allow/Deny/Exception/DefaultRestrict/ProtectedTarget/NetworkTarget, spawned `cmd.exe` live-to-exited transition, spoofed session SID/LUID, spoofed process SID/start-time PID reuse, mismatched SHA-256/missing executable/junction alias/directory path, invalid/non-existent PIDs, historical timestamp rejection, non-Windows platform simulation, simulated/caller-supplied verifier rejection, and cancellation propagation).
- **Updated BPE-1.6.1 boundary tests:**
  - `prototypes/Boundless.Protection.Engine.Tests/AuthorizedIdentityBoundaryTests.cs`: Updated synthetic-claim assertions from pre-wiring `IdentityUnavailable` (`"trusted-identity-verifier-unavailable"`) to wired OS rejection (`IdentityRejected` on Windows with `"session-subject-sid-mismatch"` / `"invalid-session-generation"`, `IdentityUnsupported` on non-Windows).
  - `prototypes/Boundless.Protection.Engine.Tests/IdentityTrustBoundaryTests.cs`: Verified `IdentityUnavailable` via the `null` verifier internal path and wired fail-closed rejection via the public entry point.
  - `prototypes/Boundless.Protection.Engine.Tests/IdentitySpoofingAndSessionBoundaryTests.cs`: Added `typeof(WindowsIdentityBoundaryVerifier)` to the OS-backed verifier type allowlist in `Reflection_UntrustedCallerClaimsCanNeverBePromotedToVerifiedEvidenceWithoutOsVerification`.

---

## 3. Verification Summary

- **Solution Release build (`BoundlessStudy.slnx`):** Succeeded with **0 warnings, 0 errors**.
- **`Boundless.Protection.Engine.Tests` (Release):** **255 / 255 passed**, **0 failed**, **0 skipped**, **0 warnings**, **0 errors**.
- **`Boundless.Protection.Engine.Tests` (Debug):** Verified clean build and execution (see `BPE-1.7-Test-Evidence.md`).
