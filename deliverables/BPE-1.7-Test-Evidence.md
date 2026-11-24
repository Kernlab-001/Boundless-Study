# Boundless Study — BPE-1.7 Test Evidence

**Date:** 2026-11-24  
**Runtime / SDK:** .NET 10.0 (`net10.0`, `Microsoft.NETCore.App 10.0.15`, xUnit.net VSTest Adapter v3.1.4+50e68bbb8b)  
**Host OS:** Windows  
**Build Policy (`Directory.Build.props`):** `Nullable=enable`, `TreatWarningsAsErrors=true`, `AnalysisLevel=latest`

---

## 1. Build Verification

### 1.1 Full Solution Release Build (Post-Final Edit)

```text
dotnet build "e:\Booundless Study\bpe13-work\BoundlessStudy.slnx" -c Release --no-restore -m:1 -v:minimal
```

Result:
- All 15 projects in `BoundlessStudy.slnx` built cleanly (`Boundless.Protection.Engine`, `Boundless.Protection.Engine.CrashWorker`, `Boundless.Protection.Engine.Tests`, plus all 8 `src/` and 4 `tests/` projects).
- **Build succeeded: 0 Warning(s), 0 Error(s)** (`Time Elapsed 00:00:05.44`).

---

## 2. Test Execution Summary (`Boundless.Protection.Engine.Tests`)

### 2.1 Release Configuration (Post-Final Edit)

```text
dotnet test "e:\Booundless Study\bpe13-work\prototypes\Boundless.Protection.Engine.Tests\Boundless.Protection.Engine.Tests.csproj" -c Release --no-build --no-restore --verbosity normal
```

```text
Test Run Successful.
Total tests: 255
     Passed: 255
     Failed: 0
    Skipped: 0
 Total time: 16.3850 Seconds
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 2.2 Debug Configuration (Post-Final Edit)

```text
dotnet test "e:\Booundless Study\bpe13-work\prototypes\Boundless.Protection.Engine.Tests\Boundless.Protection.Engine.Tests.csproj" -c Debug --no-restore --verbosity minimal
```

```text
Passed!  - Failed:     0, Passed:   255, Skipped:     0, Total:   255, Duration: 15 s - Boundless.Protection.Engine.Tests.dll (net10.0)
```

---

## 3. Suite Breakdown Across All 18 Test Classes (255 / 255 Passed)

| Test Suite (Class) | Passed | Failed | Skipped | Total | Coverage Scope |
|---|---:|---:|---:|---:|---|
| `AuthorizedIdentityBoundaryTests` | 6 | 0 | 0 | 6 | Public authorization boundary, fail-closed forged claims, reflection API boundary, cancellation |
| `DurableCorruptionAndOwnershipTests` | 7 | 0 | 0 | 7 | SQLite journal/provider corruption & rule ownership boundaries |
| `DurableStoreTests` | 1 | 0 | 0 | 1 | SQLite durable apply/restore persistence |
| `ExecutablePathPreflightTests` | 27 | 0 | 0 | 27 | Lexical Windows path preflight & ambiguity rejection |
| `FilesystemAliasAndReplacementRaceTests` | 12 | 0 | 0 | 12 | Junctions, symlinks, hard links, 8.3 names, ADS, subst, TOCTOU file replacement races |
| `IdentitySpoofingAndSessionBoundaryTests` | 51 | 0 | 0 | 51 | Spoofed SID/LUID, cross-session, expiration, generation tampering, reflection promoter checks |
| `IdentityTrustBoundaryTests` | 4 | 0 | 0 | 4 | Claim-to-evidence boundary, untrusted simulator rejection, null-verifier unavailable fallback |
| `PolicyEvaluationAdversarialTests` | 11 | 0 | 0 | 11 | Rule precedence, stale/inactive/mismatched snapshots, protected targets, network rules |
| `PolicyPathIdentityTests` | 7 | 0 | 0 | 7 | Ambiguous target path fail-closed & simulation-only reporting |
| `ProcessCrashRecoveryTests` | 6 | 0 | 0 | 6 | Out-of-process synthetic `CrashWorker` abrupt crash & cross-process lock recovery |
| `ProcessIdentityRaceConditionTests` | 21 | 0 | 0 | 21 | PID reuse, exit-code 259, double-check handle revalidation, cross-user/session races |
| `ProtectionEngineFailureInjectionTests` | 15 | 0 | 0 | 15 | Provider fault injection, conflict/partial restore, invalid state transitions |
| `ProviderCapabilityAdversarialTests` | 4 | 0 | 0 | 4 | Capability discovery, typed error/unsupported states, restore cancellation recovery |
| `SimulationClaimTests` | 1 | 0 | 0 | 1 | `IsSimulation` / `CanClaimProtection == false` invariants |
| `WindowsAuthorizedEvaluationTests` | 16 | 0 | 0 | 16 | **New BPE-1.7 E2E suite:** live Windows session/process/executable authorized evaluation & fail-closed checks |
| `WindowsExecutableFileIdentityTests` | 9 | 0 | 0 | 9 | Single-handle Win32 file identity resolution, hashing, ACL denied, sharing violations |
| `WindowsProcessIdentityVerifierTests` | 33 | 0 | 0 | 33 | Win32 `OpenProcess` / `GetProcessTimes` / token SID / session checks & probe tests |
| `WindowsSessionIdentityVerifierTests` | 24 | 0 | 0 | 24 | Win32 `OpenProcessToken` SID/LUID/SessionId verification & negative cases |
| **Total** | **255** | **0** | **0** | **255** | **All suites passing** |

---

## 4. Detailed Log — `WindowsAuthorizedEvaluationTests` (Release Run)

All 16 test cases in `WindowsAuthorizedEvaluationTests` executed on live Windows OS identity, processes, and executables:

```text
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.DefaultBoundaryVerifier_ExposesOperatingSystemVerifiedTrust [2 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_AndBoundaryVerifier_PropagateCancellation [3 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_HistoricalEvaluatedAtUtcBeforeVerification_FailsClosed [3 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_InvalidProcessId_FailsClosed(invalidPid: 0) [3 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_InvalidProcessId_FailsClosed(invalidPid: -1) [2 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_InvalidProcessId_FailsClosed(invalidPid: -999) [4 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_MismatchedExecutableHashOrPathOrAlias_FailsClosed [361 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_NonExistentProcessId_FailsClosed [8 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_RealCurrentProcessAndExecutable_EvaluatesAllowDenyExceptionAndDefaultRestrict [22 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_RealProcessWithNetworkTargetAndProtectedExclusion_EvaluatesCorrectly [34 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_RealSpawnedChildProcess_AllowsWhileRunningAndFailsClosedAfterExit [299 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_SpoofedProcessSidOrStartTime_FailsClosed [4 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_SpoofedSessionSidOrAuthContext_FailsClosed [4 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_WhenNonWindowsPlatformSimulated_FailsClosedAsUnsupported [2 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_WithSimulatedOrCallerSuppliedVerifier_FailsClosedWithoutCallingVerifier(untrustedSource: CallerSupplied) [1 ms]
Passed Boundless.Protection.Engine.Tests.WindowsAuthorizedEvaluationTests.EvaluateAuthorizedAsync_WithSimulatedOrCallerSuppliedVerifier_FailsClosedWithoutCallingVerifier(untrustedSource: SimulatedUntrusted) [4 ms]
```

---

## 5. Pre-Wiring vs. Post-Wiring Verification Notes

When `WindowsIdentityBoundaryVerifier.Default` was wired into `ProtectionPolicyEvaluator.EvaluateAuthorizedAsync`, an intermediate test run caught the expected 6 legacy BPE-1.6.1 test cases that previously asserted `PolicyEvaluationStatus.IdentityUnavailable` (`"trusted-identity-verifier-unavailable"`) or excluded `WindowsIdentityBoundaryVerifier` from the OS-backed verifier reflection allowlist:
- `AuthorizedIdentityBoundaryTests.PublicAuthorizationEntryFailsClosedForCallerClaims` (3 theory rows)
- `AuthorizedIdentityBoundaryTests.NoTargetOrRuleCanTurnSimulatedClaimsIntoAuthorization`
- `IdentityTrustBoundaryTests.AuthorizedEvaluationWithoutTrustedVerifierReturnsUnavailableAndFailsClosed`
- `IdentitySpoofingAndSessionBoundaryTests.Reflection_UntrustedCallerClaimsCanNeverBePromotedToVerifiedEvidenceWithoutOsVerification`

Those assertions were updated for BPE-1.7 (preserving all fail-closed, simulation-only, and non-public-promoter invariants, and routing the `null` verifier test to the internal `EvaluateAuthorizedAsync(null, ...)` overload), after which both Debug and Release runs passed **255 / 255** with **0 warnings** and **0 errors**.
