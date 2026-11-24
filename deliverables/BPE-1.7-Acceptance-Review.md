# Boundless Study — BPE-1.7 Acceptance Review

**Reviewer Role:** Independent Integration, Testing & Acceptance Reviewer (BPE-1.7)  
**Review Date:** 2026-11-24  
**Verdict:** **PASS**

---

## 1. Executive Summary

BPE-1.7 completes the integration of the Windows OS-backed session, process, and executable identity verifiers behind `IIdentityBoundaryVerifier` via `WindowsIdentityBoundaryVerifier` and connects them to the public `ProtectionPolicyEvaluator.EvaluateAuthorizedAsync` entry point.

All BPE-1.7 objectives, fail-closed security invariants, compilation quality gates (`TreatWarningsAsErrors=true`, `Nullable=enable`, `AnalysisLevel=latest`), and end-to-end Windows verification tests pass with **0 warnings, 0 errors, and 255 / 255 passing tests** across all 18 test suites in `Boundless.Protection.Engine.Tests`.

---

## 2. Requirements & Verification Matrix

| # | Requirement | Implementation & Evidence | Status |
|---|---|---|---|
| **1.1** | `WindowsIdentityBoundaryVerifier` implements `IIdentityBoundaryVerifier` | `prototypes/Boundless.Protection.Engine/WindowsIdentityBoundaryVerifier.cs`: `public sealed class WindowsIdentityBoundaryVerifier : IIdentityBoundaryVerifier` | **PASS** |
| **1.2** | `SourceTrust = IdentityEvidenceSourceTrust.OperatingSystemVerified` | `WindowsIdentityBoundaryVerifier.SourceTrust` returns `IdentityEvidenceSourceTrust.OperatingSystemVerified` | **PASS** |
| **1.3** | Combines `WindowsSessionIdentityVerifier`, `WindowsProcessIdentityVerifier`, and `WindowsExecutableIdentityVerifier` | Fields `_sessionVerifier`, `_processVerifier`, `_executableVerifier` initialized in public parameterless constructor and injectable via internal constructor | **PASS** |
| **1.4** | `VerifySessionAsync(claims, token)` delegates to `WindowsSessionIdentityVerifier` | Enforces null, cancellation, and `_isWindows()` platform checks, then delegates to `_sessionVerifier.VerifySessionAsync(claims, cancellationToken)` | **PASS** |
| **1.5** | `VerifyProcessAsync(claims, session, token)` verifies process + executable and fails closed with corresponding status/reason | Verifies process via `_processVerifier.VerifyProcessAsync(claims, session, token)` and executable via `_executableVerifier.VerifyAsync(claims.ExecutablePath, claims.ExecutableSha256, token)`; preserves `Unavailable`/`Unsupported`/`Rejected` status & reason codes on either failure; returns OS-verified `VerifiedProcessIdentity` on success | **PASS** |
| **1.6** | Exposes `public static WindowsIdentityBoundaryVerifier Default => new WindowsIdentityBoundaryVerifier()` | Declared as `public static WindowsIdentityBoundaryVerifier Default => new WindowsIdentityBoundaryVerifier();` and verified by `DefaultBoundaryVerifier_ExposesOperatingSystemVerifiedTrust` | **PASS** |
| **2.1** | Public `ProtectionPolicyEvaluator.EvaluateAuthorizedAsync` uses `_identityVerifier ?? WindowsIdentityBoundaryVerifier.Default` on Windows | `prototypes/Boundless.Protection.Engine/PolicyEvaluation.cs`: routes `_isWindows()` path to `_identityVerifier ?? WindowsIdentityBoundaryVerifier.Default` | **PASS** |
| **2.2** | On non-Windows, returns `Closed(PolicyEvaluationStatus.IdentityUnsupported, "platform-unsupported-windows-identity-required")` | Enforced in public `EvaluateAuthorizedAsync` when `!_isWindows()` and verified by `EvaluateAuthorizedAsync_WhenNonWindowsPlatformSimulated_FailsClosedAsUnsupported` | **PASS** |
| **2.3** | Simulated / untrusted sources remain simulation-only and cannot authorize | `PolicyEvaluationResult.IsSimulation` remains `true`, `CanClaimProtection` remains `false`; `SourceTrust != OperatingSystemVerified` fails closed with `IdentityUnsupported` (`"verifier-source-not-trusted"`) before calling verifier methods | **PASS** |
| **2.4** | Constructor overload allows test harnesses to supply an `IIdentityBoundaryVerifier` without exposing it publicly | Added `internal ProtectionPolicyEvaluator(ProtectedTargetCatalog, TimeSpan?, IIdentityBoundaryVerifier?, Func<bool>?)`; `IIdentityBoundaryVerifier` remains `internal` and public API reflection invariants pass | **PASS** |
| **3.1** | E2E tests: real Windows identity, real running process, real executable yielding Allow / Deny | Verified for both current process (`dotnet` / `testhost`) and spawned `cmd.exe` child process across Allow, Deny, Exception, Default Restrict, Network target, and Protected Target exclusion | **PASS** |
| **3.2** | E2E tests: mismatched identity / spoofed claims fail closed | Verified for spoofed session SID, tampered logon LUID, spoofed process owner SID, mismatched process start time (PID reuse), and historical `evaluatedAtUtc` | **PASS** |
| **3.3** | E2E tests: mismatched executable / hash fail closed | Verified for tampered SHA-256, non-existent file, directory junction alias path, and directory target | **PASS** |
| **3.4** | E2E tests: non-existent / exited process fail closed | Verified for invalid PIDs (`0`, `-1`, `-999`), non-existent PID, exited `cmd.exe` with exit code `259` (`STILL_ACTIVE`) while handle open (`process-already-exited`), and after process handle disposal (`process-not-found` / `process-already-exited`) | **PASS** |
| **3.5** | E2E tests: non-Windows simulation & fail-closed checks | Verified via `isWindows: () => false` on both `ProtectionPolicyEvaluator` and `WindowsIdentityBoundaryVerifier`, plus `SimulatedUntrusted` and `CallerSupplied` verifier rejection | **PASS** |
| **4** | Compile with 0 warnings, 0 errors, all tests pass, deliverables generated | Release solution build (`0 warnings, 0 errors`), Release & Debug `Boundless.Protection.Engine.Tests` (`255/255 passed, 0 failed, 0 skipped`), and all three BPE-1.7 deliverables written | **PASS** |

---

## 3. Independent Security & Integration Observations

1. **Trust Boundary Integrity Preserved:**
   - `IIdentityBoundaryVerifier` and `IdentityTrustBoundary` remain strictly `internal`.
   - `VerifiedSessionIdentity`, `VerifiedProcessIdentity`, `VerifiedExecutableIdentity`, and `IdentityVerificationResult<T>.Verified(...)` remain `internal`-constructed; no public API accepts caller-selected verifiers or promotes caller claims without OS verification.
   - `AuthorizedIdentityBoundaryTests.PublicApiDoesNotAcceptCallerSelectedVerifierOrExposeTrustedIssuer` and `IdentitySpoofingAndSessionBoundaryTests.Reflection_UntrustedCallerClaimsCanNeverBePromotedToVerifiedEvidenceWithoutOsVerification` both pass.
2. **Live Verification Timestamp Reconciliation:**
   - Verified a subtle integration edge case: when a caller passes `DateTimeOffset.UtcNow` at the call site of `EvaluateAuthorizedAsync`, Win32 token inspection and single-handle SHA-256 hashing advance wall-clock time by a few milliseconds beyond the caller's `evaluatedAtUtc`.
   - `ResolveLiveVerificationTimestamp` in `PolicyEvaluation.cs` narrowly resolves this for live `WindowsIdentityBoundaryVerifier` executions only when `evaluatedAtUtc` is UTC and within 250 ms of verification start, while keeping strict fail-closed rejection for historical, future-expired, stale, or non-UTC timestamps.
3. **Retained Scope Boundaries (Informational for Future BPE-2):**
   - **No Active OS Enforcement:** `ProtectionPolicyEvaluator` still computes decisions only (`IsSimulation == true`, `CanClaimProtection == false`); no AppLocker, WFP, firewall, or Windows service policy is applied.
   - **Process-Handle-to-Image-Path Binding:** `WindowsProcessIdentityVerifier` binds the live process handle's PID, kernel start time, token SID, and session ID, while `WindowsExecutableIdentityVerifier` binds the claimed `ExecutablePath` to its canonical DOS path and SHA-256 on a single file handle. Binding the live process handle's kernel image path (e.g., via `QueryFullProcessImageNameW`) directly to `claims.ExecutablePath` is recommended for future BPE-2 enforcement integration.

---

## 4. Final Acceptance Decision

- **Verdict:** **PASS**
- **Basis:** All BPE-1.7 implementation, integration, security boundary, build quality (0 warnings / 0 errors), E2E Windows verification (255 / 255 passing tests), and acceptance documentation criteria are satisfied.
