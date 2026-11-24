# BPE-1.7 Independent Security Review

**Audit scope:** BPE-1.7 Windows trust foundation (`Boundless.Protection.Engine`, `Boundless.Protection.Engine.CrashWorker`, and `Boundless.Protection.Engine.Tests`)  
**Audit date:** 2026-11-24  
**Auditor:** Subagent D1 (Security, Compatibility, and Dependency Auditor)  
**Security verdict:** **PASS WITH CONDITIONS (Bounded to BPE-1.7 OS-Identity Verification Primitives)**

---

## 1. Executive Summary and Security Verdict

BPE-1.7 implements the three Windows operating-system identity verification primitives designed to replace caller-supplied identity assertions in future enforcement milestones:

1. [`WindowsSessionIdentityVerifier`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSessionIdentityVerifier.cs) — verifies caller session claims against the current Windows process token (`TokenUser` SID, `TokenStatistics.AuthenticationId` logon LUID, and `TokenSessionId`).
2. [`WindowsProcessIdentityVerifier`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs) — binds a live PID to its kernel creation timestamp, process token owner SID, and Windows session ID over a single pinned handle, with same-handle double-check liveness revalidation before issuing evidence.
3. [`WindowsExecutableIdentityVerifier`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs) — opens an executable once without write sharing, resolves its canonical DOS path via `GetFinalPathNameByHandleW`, reads NTFS/ReFS file identifiers, hashes the file directly from the open `SafeFileHandle`, and re-verifies the handle's final path post-hash to defeat TOCTOU replacement.

All Win32 P/Invoke bindings are isolated in [`WindowsSecurityApis.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSecurityApis.cs) using `SafeHandle` subclasses (`SafeProcessHandle`, `SafeFileHandle`, `SafeProcessTokenHandle`, `SafeLocalAllocHandle`) with deterministic `using` disposal.

### Explicit Verdict

**PASS WITH CONDITIONS** for the BPE-1.7 Windows trust-foundation milestone:

- **Condition 1 — Public Authorization Entry Point Remains Intentionally Unwired:** [`ProtectionPolicyEvaluator.EvaluateAuthorizedAsync`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/PolicyEvaluation.cs#L264-L276) still fails closed with `IdentityUnavailable("trusted-identity-verifier-unavailable")`. No production `IIdentityBoundaryVerifier` composes the three verifiers yet, and `CanClaimProtection` remains hardcoded to `false`.
- **Condition 2 — Process-to-Executable Kernel Binding Gap Before Composition:** [`WindowsProcessIdentityVerifier.VerifyProcess`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs#L190-L198) validates PID liveness, start time, owner SID, and session ID, but populates `VerifiedProcessIdentity.ExecutablePath` and `ExecutableSha256` from `claims` without invoking `QueryFullProcessImageNameW` or `WindowsExecutableIdentityVerifier`. Before wiring `EvaluateAuthorizedAsync` for production authorization, a composite `IIdentityBoundaryVerifier` **must** extract the process image path from the pinned `SafeProcessHandle` and verify it through `WindowsExecutableIdentityVerifier.VerifyAsync`.
- **Condition 3 — Persistent Store Encryption & Enforcement Scope:** Consistent with [BPE-1.6.3-Encryption-Resolution.md](file:///E:/Booundless%20Study/bpe13-work/deliverables/BPE-1.6.3-Encryption-Resolution.md), SQLite persistence remains synthetic-only and unencrypted until SQLCipher licensing/DPAPI gates pass, and no AppLocker/WFP enforcement providers are implemented.

---

## 2. Trust Boundary & Prevention of Caller Claim Promotion

The BPE-1.7 trust boundary in [`IdentityTrustBoundary.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/IdentityTrustBoundary.cs) enforces strict type-level separation between untrusted caller claims (`CallerSessionIdentityClaims`, `CallerProcessIdentityClaims`) and OS-verified evidence (`VerifiedSessionIdentity`, `VerifiedProcessIdentity`, `VerifiedExecutableIdentity`):

| Control | Implementation Evidence | Security Effect |
|---|---|---|
| Sealed evidence classes with `internal` constructors | [`IdentityTrustBoundary.cs#L46-L111`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/IdentityTrustBoundary.cs#L46-L111), [`WindowsExecutableIdentityVerifier.cs#L13-L43`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs#L13-L43) | External callers cannot instantiate or mutate `Verified*` evidence; no public setters or implicit/explicit conversion operators exist. |
| Gated `IdentityVerificationResult<T>` construction | [`IdentityTrustBoundary.cs#L119-L161`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/IdentityTrustBoundary.cs#L119-L161) | Constructor is `private`; `Verified`, `Rejected`, `Unavailable`, and `Unsupported` factories are `internal`. Only `Simulated(...)` is `public`, and it sets `Status = Rejected`, `SourceTrust = SimulatedUntrusted`, `Evidence = null`, and `IsTrusted = false`. |
| Internal verifier contract & trust gate | [`IdentityTrustBoundary.cs#L175-L213`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/IdentityTrustBoundary.cs#L175-L213) | `IIdentityBoundaryVerifier` and `IdentityTrustBoundary` are `internal`. `IsTrustedSession` and `IsTrustedProcess` require `SourceTrust == OperatingSystemVerified`, `IsTrusted == true`, non-null evidence, and matching `SessionId`, `SessionGeneration`, and `SubjectSid`. |
| Parameterless OS-backed public constructors | [`WindowsSessionIdentityVerifier.cs#L20`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSessionIdentityVerifier.cs#L20), [`WindowsProcessIdentityVerifier.cs#L19`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs#L19) | Mock/probe injection constructors are strictly `internal` (accessible only to `Boundless.Protection.Engine.Tests` via `InternalsVisibleTo`). Production callers cannot inject fake OS probes. |
| Reflection invariant verification | [`IdentitySpoofingAndSessionBoundaryTests.cs#L450-L540`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/IdentitySpoofingAndSessionBoundaryTests.cs#L450-L540) | Automated reflection audit confirms no public assembly type outside the three verifiers accepts caller claim types and returns `Verified*` or `IdentityVerificationResult<Verified*>`. |

---

## 3. Windows User & Session Identity Verification (`WindowsSessionIdentityVerifier`)

### 3.1 Mechanism Analysis

[`WindowsSessionIdentityVerifier`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSessionIdentityVerifier.cs) authenticates caller session claims against the kernel primary token of the current process:

1. **Platform & Input Preconditions:** Fails closed with `Unsupported("platform-unsupported-windows-identity-required")` when `!_isWindows()`. Rejects `Guid.Empty` (`"invalid-session-id"`) and `SessionGeneration <= 0` (`"invalid-session-generation"`).
2. **Kernel Token Inspection (`TryReadCurrentProcessTokenIdentity`):**
   - Calls `OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out tokenHandle)` (`advapi32.dll`).
   - Queries `TokenUser` using a two-call size probe requiring `ERROR_INSUFFICIENT_BUFFER (122)`, then converts the binary SID to canonical string form via `ConvertSidToStringSidW` wrapped in `SafeLocalAllocHandle` (`LocalFree`).
   - Queries `TokenStatistics` (`TOKEN_STATISTICS`) to extract `AuthenticationId` (the kernel-assigned 64-bit logon LUID formatted as `0x{...}` lowercase hex).
   - Queries `TokenSessionId` to extract the 32-bit Windows Terminal Services session ID (`uint`).
3. **Claim Binding & Fail-Closed Rejection:**
   - Any Win32 token error or empty attribute returns `Unavailable("os-identity-unavailable: ...")`.
   - `claims.SubjectSid` must match `osIdentity.UserSid` using exact `StringComparison.Ordinal` (`Rejected("session-subject-sid-mismatch")`), rejecting case variations, leading/trailing whitespace, or well-known privilege SIDs (`S-1-5-18`, `S-1-5-32-544`, etc.).
   - If `claims.AuthenticationContext` is non-null, it must match `osIdentity.AuthenticationId` via `StringComparison.Ordinal` (`Rejected("session-auth-context-mismatch")`). If omitted (`null`), `VerifiedSessionIdentity.AuthenticationContext` is populated directly from the OS logon LUID (`osIdentity.AuthenticationId`), never from caller strings.
4. **Session Lifetime & UTC Normalization:** Normalizes `_utcNow().ToUniversalTime()` (`TimeSpan.Zero`) and sets `ExpiresAtUtc = authenticatedAtUtc.Add(_sessionLifetime)` (default 8 hours; constructor rejects `<= TimeSpan.Zero`).

### 3.2 Findings & Observations

- **S-1 (Low / Architectural Note — Terminal Session ID Propagation):** `TryReadCurrentProcessTokenIdentity` reads `WindowsTokenSessionIdentity.SessionId` (verified against `Process.GetCurrentProcess().SessionId` in tests), but [`VerifiedSessionIdentity`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/IdentityTrustBoundary.cs#L46-L73) does not expose a `WindowsSessionId` property. Consequently, `WindowsProcessIdentityVerifier` checks internal session consistency on the target process (`TokenSessionId == ProcessIdToSessionId(pid)`), rather than requiring the target process to share the verifier's own Terminal Session ID. While appropriate if a Session-0 service later verifies interactive Session-N processes, same-user processes across concurrent RDP/Fast-User-Switching sessions are distinguished by `SessionId`/`AuthenticationContext` rather than `WindowsSessionId`.
- **S-2 (Informational — Primary Token Scope):** `OpenProcessToken(GetCurrentProcess(), ...)` reads the process primary token rather than thread impersonation tokens (`OpenThreadToken`). This is the correct design for authenticating the host process's OS identity and avoids thread-impersonation confusion.
- **S-3 (Informational — `LUID.ToString()` Signed Cast):** In [`WindowsSecurityApis.cs#L101`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSecurityApis.cs#L101), `((ulong)HighPart << 32) | LowPart` sign-extends negative `int HighPart` before shifting left 32 bits; the lower 32 bits are shifted into bits 32..63 so the resulting 64-bit value is bitwise accurate, though casting `(uint)HighPart` would be clearer.

---

## 4. Live Process Identity Verification (`WindowsProcessIdentityVerifier`)

### 4.1 Mechanism Analysis

[`WindowsProcessIdentityVerifier`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs) addresses PID volatility, PID reuse, zombie/exited handles, and cross-user/cross-session spoofing:

1. **Least-Privilege Single Handle Acquisition:**
   - Rejects `claims.ProcessId <= 0` (`"invalid-process-id"`) and invalid session evidence (`SessionId == Guid.Empty || Generation <= 0` -> `"invalid-session-identity"`).
   - Opens the target PID **once** with `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION [0x1000], bInheritHandle: false, claims.ProcessId)`.
   - Maps `ERROR_ACCESS_DENIED (5)` -> `Unavailable("process-access-denied")` and `ERROR_INVALID_PARAMETER (87)` -> `Unavailable("process-not-found")`.
2. **Kernel Creation-Time Binding (PID-Reuse Defense):**
   - Queries `GetProcessTimes` on the pinned `SafeProcessHandle` and compares `actualStartUtc` against `claims.ProcessStartUtc.ToUniversalTime()`.
   - Initial check allows `StartTimeTolerance = 1 ms` (`10,000` ticks) to tolerate managed `Process.StartTime` / caller clock conversion granularity; differences `> 1 ms` fail closed with `Rejected("process-pid-reused-or-start-time-mismatch")`.
3. **Triple Liveness Check (`CheckProcessLiveness`):**
   - **Check 1 (`lpExitTime`):** Fails closed (`Unavailable("process-already-exited")`) if `lpExitTime.dwLowDateTime != 0 || lpExitTime.dwHighDateTime != 0`. This defeats the classic Win32 `STILL_ACTIVE` (259) trap where a process exits with exit code `259` (`exit 259`).
   - **Check 2 (`GetExitCodeProcess`):** Fails closed if `exitCode != STILL_ACTIVE (259)`.
   - **Check 3 (`WaitForSingleObject(hProcess, 0)`):** Fails closed if `WAIT_OBJECT_0`. Because `OpenProcess` intentionally uses `PROCESS_QUERY_LIMITED_INFORMATION` without `SYNCHRONIZE`, `WaitForSingleObject` can return `WAIT_FAILED` with `ERROR_ACCESS_DENIED (5)`; `CheckProcessLiveness` explicitly tolerates `WAIT_FAILED` only when `waitError == ERROR_ACCESS_DENIED` (relying on `lpExitTime` and `GetExitCodeProcess`), and fails closed on any other `WAIT_FAILED` error.
4. **Process Token & Cross-Session Consistency:**
   - Calls `OpenProcessToken` on the pinned `SafeProcessHandle` (using `DangerousAddRef`/`DangerousRelease` safely in `try/finally`), queries `TokenUser`, and enforces `actualSid == session.SubjectSid && actualSid == claims.SubjectSid` (`StringComparison.Ordinal`).
   - Queries `TokenSessionId` from the pinned token handle and cross-checks it against `ProcessIdToSessionId(claims.ProcessId)` (`Rejected("process-pid-reused-or-session-mismatch")`).
   - Maps `ERROR_ACCESS_DENIED` during token open/query cleanly to `Unavailable("process-token-access-denied")`.
5. **Same-Handle Double-Check Revalidation:**
   - Before issuing `VerifiedProcessIdentity`, re-queries `GetProcessTimes` and `CheckProcessLiveness` on the **exact same `SafeProcessHandle`**.
   - Re-check enforces **exact 0-tick equality** (`recheckStartUtc.ToUniversalTime() != actualStartUtc`), catching any mid-inspection exit or state anomaly before returning trusted evidence.

### 4.2 Findings & Observations

- **P-1 (Medium / Pre-Composition Gate — Unverified Executable Claims Pass-Through):** In [`WindowsProcessIdentityVerifier.cs#L190-L198`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs#L190-L198), `VerifiedProcessIdentity` records `claims.ExecutablePath ?? string.Empty` and `claims.ExecutableSha256 ?? string.Empty` verbatim from `CallerProcessIdentityClaims`. Neither `QueryFullProcessImageNameW` nor `WindowsExecutableIdentityVerifier` is called inside `WindowsProcessIdentityVerifier`. Consequently, `WindowsProcessIdentityVerifier` authenticates *who owns the process and whether it is the exact live PID instance*, **not** *what binary image the process is executing*. Any future composite `IIdentityBoundaryVerifier` **must** query the image path from the live `SafeProcessHandle` (`QueryFullProcessImageNameW`) and verify it via `WindowsExecutableIdentityVerifier` before passing `VerifiedProcessIdentity` to `ProtectionPolicyEvaluator`.
- **P-2 (Low / Design Note — `ProcessIdToSessionId` PID-Indexed Lookup):** `ProcessIdToSessionId(uint dwProcessId, ...)` takes a PID rather than a handle, creating a narrow theoretical race if a process exited and its PID was recycled between `OpenProcess` and `ProcessIdToSessionId` under the same SID. This is mitigated by the subsequent same-handle `GetProcessTimes` + `CheckProcessLiveness` double-check (if the original process exited, the pinned handle transitions to signaled/`lpExitTime != 0` and fails closed) and by `TokenSessionId == pidSessionId` rejecting cross-session reuse.
- **P-3 (Low / Design Note — Session Expiry Not Re-Checked in `VerifyProcess`):** `WindowsProcessIdentityVerifier.VerifyProcess` validates `session.SessionId != Guid.Empty && session.Generation > 0` but does not check `session.ExpiresAtUtc`. Session lifetime enforcement relies on `ProtectionPolicyEvaluator.SessionEvidenceMatchesClaims` ([`PolicyEvaluation.cs#L334-L340`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/PolicyEvaluation.cs#L334-L340)), which is tested by A2.

---

## 5. Authoritative Executable File Identity Verification (`WindowsExecutableIdentityVerifier`)

### 5.1 Mechanism Analysis

[`WindowsExecutableIdentityVerifier`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs) resolves canonical paths, metadata, and SHA-256 digests from a single kernel file handle:

1. **Input & Platform Guards:** Rejects non-Windows (`Unsupported("windows-platform-required")`), empty/whitespace paths, and embedded NUL bytes (`Rejected("executable-path-invalid")`).
2. **Single-Handle Opening (`OpenAuthoritativeHandle`):**
   - Calls `CreateFileW(filePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero)`.
   - Deliberately **omits `FILE_SHARE_WRITE`**: any pre-existing open writer fails immediately with `ERROR_SHARING_VIOLATION (32)` -> `Unavailable("executable-sharing-violation")`, and any concurrent writer attempting to mutate bytes during verification is blocked by the OS sharing lock.
   - Maps `ERROR_FILE_NOT_FOUND (2)` / `ERROR_PATH_NOT_FOUND (3)` -> `Unavailable("executable-not-found")`, and `ERROR_ACCESS_DENIED (5)` -> `Unavailable("executable-access-denied")`.
3. **Metadata & Directory Rejection (`TryReadFileMetadata`):**
   - Queries `GetFileInformationByHandle` for `BY_HANDLE_FILE_INFORMATION` (`dwFileAttributes`, `dwVolumeSerialNumber`, 64-bit `FileIndex`, 64-bit `FileSizeBytes`).
   - Rejects directories (including directory junctions and directory symlinks) via `(fileInfo.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0` -> `Rejected("executable-is-directory")`.
   - Best-effort queries `GetFileInformationByHandleEx(FileIdInfo)` for 64-bit `ExtendedVolumeSerialNumber` and 128-bit `FileId128` (tolerates `false` on non-NTFS/ReFS filesystems).
4. **Canonical DOS Path Resolution (`TryResolveCanonicalDosPath`):**
   - Calls `GetFinalPathNameByHandleW(hFile, ..., FILE_NAME_NORMALIZED | VOLUME_NAME_DOS)` (resizing dynamically if `>= capacity`).
   - Because resolution operates on the open kernel `SafeFileHandle` with `FILE_NAME_NORMALIZED | VOLUME_NAME_DOS`, Windows resolves:
     - File and directory symbolic links (single and chained)
     - Directory junctions (single, chained, and dangling detection)
     - 8.3 short names (e.g., `PROGRA~1`, `RUNTIM~1`)
     - DOS `subst` drives (`DefineDosDeviceW`)
     - Relative/traversal spellings (`.`, `..`, forward slashes, trailing dots/spaces, `\\?\`, `\\.\`)
     - Explicit default NTFS data stream syntax (`::$DATA`)
     - Filesystem casing normalization (producing exact canonical casing)
   - Strips `\\?\` and `\\?\UNC\` prefixes via `StripExtendedDosPrefix` for clean DOS canonical representation.
5. **Stream Hashing & Post-Hash Handle Path Re-Verification:**
   - Wraps `hFile` directly in `new FileStream(hFile, FileAccess.Read)` (`ownsHandle: false`) and computes SHA-256 via `SHA256.HashData` / `SHA256.HashDataAsync`.
   - Because `FILE_SHARE_DELETE` is permitted (allowing callers/system processes to rename or POSIX-unlink an open file), `BuildVerifiedResult` calls `TryResolveCanonicalDosPath(hFile, out postHashPath)` **after** hashing completes and requires `string.Equals(canonicalPath, postHashPath, StringComparison.Ordinal)`. Any mid-hash rename (`File.Move`) or POSIX unlink (`SetFileInformationByHandle` with `FileDispositionInfoEx`) triggers `Rejected("executable-path-changed-during-verification")`.
6. **Claim Validation (`ValidateClaims`):**
   - Compares `evidence.ExecutablePath` against `claimedPath` (`StringComparison.OrdinalIgnoreCase` to match case-insensitive NTFS semantics while rejecting alias/junction/short-name/traversal spellings) and `evidence.Sha256` against `claimedSha256`.

### 5.2 Findings & Observations

- **E-1 (Low — Non-Windows `Unsupported` Reason Code Discrepancy):** [`WindowsExecutableIdentityVerifier.cs#L97,121,183`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs#L97) returns `Unsupported("windows-platform-required")`, whereas `WindowsSessionIdentityVerifier` and `WindowsProcessIdentityVerifier` return `Unsupported("platform-unsupported-windows-identity-required")`. Both fail closed (`IsTrusted == false`, `Status == Unsupported`), so security is unaffected, but unifying the reason code string is recommended for observability consistency.
- **E-2 (Informational — NTFS Hard Links Share File ID & Hash):** Multiple NTFS hard links to the same file record legitimately share `VolumeSerialNumber`, `ExtendedVolumeSerialNumber`, `FileIndex`, `FileId128`, and `Sha256`, while `GetFinalPathNameByHandleW` returns the canonical path of the specific hard-link directory entry opened. Cross-link path claims fail closed with `"executable-path-mismatch"`.
- **E-3 (Informational — UNC Path Policy vs Resolver Scope):** `StripExtendedDosPrefix` normalizes `\\?\UNC\server\share\...` to `\\server\share\...`, whereas [`ExecutablePathPreflight`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/ExecutablePathPreflight.cs#L51-L54) intentionally rejects UNC paths (`UnsupportedRoot`) for `ApplicationIdentity.IsValid` and `PolicyTarget.IsValid`. Thus UNC targets remain blocked at the policy layer even if `Resolve` inspects them.
- **E-4 (Informational — Post-Verification Execution Gap):** User-mode handle pinning prevents modification *during* `Verify`/`Resolve`, but once the `SafeFileHandle` is disposed, an executable could still be replaced before launch unless paired with kernel execution-time enforcement (AppLocker/WDAC/CI), which remains out of scope for BPE-1.7.

---

## 6. Adversarial Review Coverage (Teams A2, B2, and C2)

Independent adversarial test suites were audited against the implementation and executed live on Windows:

| Track | Test Suite | Cases Passed | Adversarial Vectors Verified |
|---|---|---:|---|
| **A2 (Identity Spoofing & Session Boundaries)** | [`IdentitySpoofingAndSessionBoundaryTests.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/IdentitySpoofingAndSessionBoundaryTests.cs) | 51 | Well-known/admin SID spoofing (`S-1-5-18`, `S-1-5-32-544`, `S-1-5-19`, `S-1-5-20`, `S-1-1-0`, RID-500, case/whitespace/null) vs scripted & real OS tokens; forged/bit-flipped logon LUIDs; `Guid.Empty` & non-positive generations; cross-session `TokenSessionId != PidSessionId`; temporal bounds (`StartsAtUtc`, `AuthenticatedAtUtc`, `ExpiresAtUtc`, non-UTC/inverted clocks); generation tampering; `SimulatedUntrusted`/`CallerSupplied` rejection; reflection audit of public API surface. |
| **B2 (Process Identity & Race Conditions)** | [`ProcessIdentityRaceConditionTests.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/ProcessIdentityRaceConditionTests.cs) | 21 | PID reuse with stale start time on real child processes & 2 ms simulated recycling; future start-time claims; cross-session PID reassignment within 1 ms tolerance; exit between `OpenProcess` & first check; exit during `OpenProcessToken`; exit with code `259` (`STILL_ACTIVE`) caught via `lpExitTime`; concurrent child kill race (5 iterations × 6 threads); `ERROR_ACCESS_DENIED` on real `PID 4` & `csrss`/`audiodg`/`smss`/`services`/`lsass`; cross-user SID mismatch; single-handle revalidation (7 ops on 1 handle, 1-tick shift detection, 16-thread concurrency). |
| **C2 (Filesystem Aliases & Replacement Races)** | [`FilesystemAliasAndReplacementRaceTests.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/FilesystemAliasAndReplacementRaceTests.cs) | 12 | Chained directory junctions & dangling junctions; file/directory symlinks (including `C:\Users\All Users -> C:\ProgramData` unprivileged fallback); DOS `subst` drives (`DefineDosDeviceW`); 8.3 short aliases (`PROGRA~1`, `RUNTIM~1`); multi-hard-link cross-path rejection; case normalization on `System32\cmd.exe` & temp paths; pre-existing & concurrent write sharing violations; mid-hash `File.Move` rename-swap via `FILE_SHARE_DELETE`; mid-hash POSIX delete/unlink (`FileDispositionInfoEx`); 40-iteration concurrent junction swap; traversal/`\\?\`/`\\.\`/trailing-dot/space/device names (`\\.\NUL`, `CON`); ADS `::$DATA` alias rejection. |

### Verification Execution Evidence

Command executed on 2026-11-24 (`Windows NT 10.0.26320.0`, `x64`, `.NET SDK 10.0.404`):

```text
dotnet test "E:\Booundless Study\bpe13-work\prototypes\Boundless.Protection.Engine.Tests\Boundless.Protection.Engine.Tests.csproj" --configuration Release --verbosity normal
```

- **Result:** `Passed! - Failed: 0, Passed: 239, Skipped: 0, Total: 239` (including 66 BPE-1.7 unit/functional tests and 84 A2/B2/C2 adversarial tests; 0 warnings, 0 errors).

---

## 7. Security Recommendations Before Production Wiring (BPE-1.8+)

1. **Bind Process Image Path to Executable Verifier (Required before wiring `EvaluateAuthorizedAsync`):** In the future composite `IIdentityBoundaryVerifier`, call `QueryFullProcessImageNameW` on the pinned `SafeProcessHandle` (prior to same-handle revalidation) and pass the resolved image path through `WindowsExecutableIdentityVerifier.VerifyAsync` rather than accepting `CallerProcessIdentityClaims.ExecutablePath` / `ExecutableSha256` verbatim.
2. **Enforce Optional Windows Terminal Session Affinity:** Decide whether `VerifiedSessionIdentity` should carry `WindowsSessionId` so interactive-session policies reject cross-Terminal-Session process claims when appropriate.
3. **Unify Non-Windows `Unsupported` Reason Code:** Align `WindowsExecutableIdentityVerifier` (`"windows-platform-required"`) with `WindowsSessionIdentityVerifier` / `WindowsProcessIdentityVerifier` (`"platform-unsupported-windows-identity-required"`).
4. **Maintain BPE-1.6.3 Storage & BPE-1.4 VM Gates:** Do not persist real OS SIDs, LUIDs, paths, or hashes to `SqliteProtectionJournal` until SQLCipher commercial licensing, DPAPI `CurrentUser` key envelopes, and disposable Windows 11 VM verification are complete.
