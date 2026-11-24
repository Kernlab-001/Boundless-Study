# BPE-1.7 Platform, OS, Architecture, and Filesystem Compatibility Report

**Audit scope:** BPE-1.7 Windows OS-identity verification foundation (`Boundless.Protection.Engine`, `Boundless.Protection.Engine.CrashWorker`, and `Boundless.Protection.Engine.Tests`)  
**Audit date:** 2026-11-24  
**Auditor:** Subagent D1 (Security, Compatibility, and Dependency Auditor)  
**Verdict:** **PASS (Windows 10 1809+ / Windows 11 / Server 2019+ on x64 & ARM64 by API contract; verified by live execution on Windows NT 10.0.26320.0 x64)**

---

## 1. Windows OS Version Support Matrix

All native bindings in [`WindowsSecurityApis.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSecurityApis.cs) and [`Program.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.CrashWorker/Program.cs) target inbox Win32 APIs exported by `advapi32.dll` and `kernel32.dll`, running on `.NET 10` (`net10.0`).

### 1.1 Minimum Win32 API Requirements

| Win32 API / Flag / Struct | DLL | Minimum Windows Client / Server | Used By | Fallback / Degradation |
|---|---|---|---|---|
| `OpenProcessToken`, `GetTokenInformation` (`TokenUser`, `TokenStatistics`, `TokenSessionId`), `ConvertSidToStringSidW` | `advapi32.dll` | Windows XP / Server 2003 | `WindowsSessionIdentityVerifier`, `WindowsProcessIdentityVerifier` | Returns `Unavailable` on Win32 failure |
| `OpenProcess` with `PROCESS_QUERY_LIMITED_INFORMATION` (`0x1000`) | `kernel32.dll` | Windows Vista / Server 2008 | `WindowsProcessIdentityVerifier` | Fail-closed `Unavailable` on `ERROR_ACCESS_DENIED` / `ERROR_INVALID_PARAMETER` |
| `GetProcessTimes`, `GetExitCodeProcess`, `WaitForSingleObject`, `ProcessIdToSessionId`, `GetCurrentProcess`, `CloseHandle`, `LocalFree` | `kernel32.dll` | Windows XP / Server 2003 | `WindowsProcessIdentityVerifier`, `WindowsSecurityApis` | `WaitForSingleObject` tolerates `WAIT_FAILED` + `ERROR_ACCESS_DENIED` on limited handles |
| `CreateFileW` (`FILE_FLAG_BACKUP_SEMANTICS`), `GetFileInformationByHandle` | `kernel32.dll` | Windows XP / Server 2003 | `WindowsExecutableIdentityVerifier` | Required; fails closed if unavailable |
| `GetFinalPathNameByHandleW` (`FILE_NAME_NORMALIZED \| VOLUME_NAME_DOS`) | `kernel32.dll` | Windows Vista / Server 2008 | `WindowsExecutableIdentityVerifier` | Required; fails closed (`executable-path-resolution-failed`) |
| `GetFileInformationByHandleEx` (`FileIdInfo` / `FILE_ID_INFO`) | `kernel32.dll` | Windows 8 / Server 2012 | `WindowsExecutableIdentityVerifier` | Best-effort optional: `ExtendedVolumeSerialNumber` and `FileId128` remain `null` if volume/FS does not support `FileIdInfo` (e.g., FAT32, exFAT) |
| `SetErrorMode` | `kernel32.dll` | Windows XP / Server 2003 | `CrashWorker` | Guarded by `OperatingSystem.IsWindows()` |

### 1.2 Target OS Matrix

| Operating System | Build / Release | Support Status | Verification Method | Notes |
|---|---|---|---|---|
| **Windows 10 (1809+)** | Build 17763+ | Supported | Win32 API & `.NET 10` lifecycle contract analysis | All Win32 APIs (`PROCESS_QUERY_LIMITED_INFORMATION`, `GetFinalPathNameByHandleW`, `FileIdInfo`, POSIX unlink semantics) are inbox; `.NET 10` supports Windows 10 1607+/1809+. Not booted locally. |
| **Windows 11 (21H2, 22H2, 23H2, 24H2)** | Builds 22000–26100+ | Supported | Win32 API contract analysis + host test execution | All Win32 APIs and `.NET 10` runtime supported. Host machine (`Windows NT 10.0.26320.0`, `DisplayVersion=26H2`) executed all 239 tests cleanly. |
| **Windows Server 2019** | Build 17763 | Supported | Win32 API & `.NET 10` contract analysis | Shares Windows 10 1809 kernel/Win32 API baseline; not booted locally. |
| **Windows Server 2022** | Build 20348 | Supported | Win32 API & `.NET 10` contract analysis | Full Win32 API and `.NET 10` support; not booted locally. |
| **Windows Server 2025** | Build 26100 | Supported | Win32 API & `.NET 10` contract analysis | Full Win32 API and `.NET 10` support; not booted locally. |
| **Linux / macOS / Non-Windows** | Any | Explicitly Unsupported (Fail-Closed) | Unit tests (`_isWindows = () => false`) + code inspection | All three OS verifiers return `IdentityVerificationStatus.Unsupported` before invoking any P/Invoke; `IsTrusted == false`. |

*Local Execution Environment Verified (2026-11-24):* `Windows NT 10.0.26320.0`, `X64`, `.NET SDK 10.0.404`, `.NET Runtime 10.0.15` — `Boundless.Protection.Engine.Tests` passed **239 / 239** (`0` failed, `0` skipped).

---

## 2. Architecture Support (`x64` and `ARM64`)

1. **Managed Compilation Target:** [`Boundless.Protection.Engine.csproj`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/Boundless.Protection.Engine.csproj), [`Boundless.Protection.Engine.CrashWorker.csproj`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.CrashWorker/Boundless.Protection.Engine.CrashWorker.csproj), and [`Boundless.Protection.Engine.Tests.csproj`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/Boundless.Protection.Engine.Tests.csproj) target `<TargetFramework>net10.0</TargetFramework>` with no `<PlatformTarget>` lock (AnyCPU IL).
2. **P/Invoke Struct & ABI Alignment Audit (`WindowsSecurityApis.cs`):**
   - `LUID` (`uint` + `int`, 8 bytes, 4-byte aligned), `FILETIME` (`uint` + `uint`, 8 bytes), and `BY_HANDLE_FILE_INFORMATION` (52 bytes, 4-byte aligned) have identical layouts on `x64` and `ARM64`.
   - `SID_AND_ATTRIBUTES` (`IntPtr Sid; uint Attributes;`) and `TOKEN_USER` use `[StructLayout(LayoutKind.Sequential)]`, yielding 16-byte pointer-aligned layout on both 64-bit `x64` and `ARM64` (and 8-byte on 32-bit x86).
   - `TOKEN_STATISTICS` (`LUID`, `LUID`, `long`, 6 × `uint`, `LUID` = 56 bytes, 8-byte aligned) and `FILE_ID_INFO` (`ulong` + 16-byte `FILE_ID_128` = 24 bytes, 8-byte aligned) match the Windows SDK headers across `x64` and `ARM64`.
   - All `DllImport` declarations use default `CallingConvention.Winapi`, which maps cleanly to the native Windows ABI on `x64` and `ARM64` (`kernel32.dll` and `advapi32.dll` are native system DLLs on Windows ARM64).
3. **Transitive Native SQLite Binary:** `SQLitePCLRaw.lib.e_sqlite3` (`2.1.13`) ships native `e_sqlite3.dll` runtime assets for `win-x64`, `win-arm64`, and `win-x86`.
4. **Verification Limitation:** Live test execution was performed on `win-x64` only; `win-arm64` execution has not yet been run on ARM64 hardware or a Windows 11 ARM64 VM.

---

## 3. WOW64 File System Redirection (`System32` vs `SysWOW64` vs `Sysnative`)

Windows applies automatic **WOW64 file system redirection** only to **32-bit (x86) processes** running on 64-bit Windows (`x64` or `ARM64`), redirecting `%SystemRoot%\System32` accesses to `%SystemRoot%\SysWOW64` unless `%SystemRoot%\Sysnative` is used.

| Host Process Bitness | Path Opened via `CreateFileW` | OS Target Opened | `GetFinalPathNameByHandleW` Canonical Output | `WindowsExecutableIdentityVerifier.Verify` Behavior |
|---|---|---|---|---|
| **Native 64-bit (`x64` / `ARM64`)** | `C:\Windows\System32\cmd.exe` | 64-bit `System32\cmd.exe` | `C:\Windows\System32\cmd.exe` | Matches `System32` claim; no redirection occurs. |
| **Native 64-bit (`x64` / `ARM64`)** | `C:\Windows\SysWOW64\cmd.exe` | 32-bit `SysWOW64\cmd.exe` | `C:\Windows\SysWOW64\cmd.exe` | Matches `SysWOW64` claim. (`Sysnative` does not exist for native 64-bit processes.) |
| **32-bit WOW64 (`x86` on 64-bit OS)** | `C:\Windows\System32\cmd.exe` | Redirected to 32-bit `SysWOW64\cmd.exe` | `C:\Windows\SysWOW64\cmd.exe` | **Fails closed** with `Rejected("executable-path-mismatch")` if `claimedPath` was `C:\Windows\System32\cmd.exe`, preventing silent WOW64 alias spoofing! |
| **32-bit WOW64 (`x86` on 64-bit OS)** | `C:\Windows\Sysnative\cmd.exe` | Bypasses redirection -> 64-bit `System32\cmd.exe` | `C:\Windows\System32\cmd.exe` | Matches canonical `C:\Windows\System32\cmd.exe` claim; rejects `Sysnative` spelling as `claimedPath`. |

### Architectural Guidance for WOW64

- `WindowsExecutableIdentityVerifier` intentionally **does not** call `Wow64DisableWow64FsRedirection` / `Wow64RevertWow64FsRedirection`, because disabling WOW64 redirection mutates per-thread OS state and is unsafe in reusable library/async thread-pool code.
- Instead, opening the file handle and resolving `GetFinalPathNameByHandleW` reveals the **actual physical file** opened by the kernel, defeating WOW64 path confusion by failing closed if a 32-bit caller claims `System32` when WOW64 opened `SysWOW64`.
- Consistent with [`ProtectedTargetCatalog.ForWindowsDirectory`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/PolicyEvaluation.cs#L211-L229), production host services and agents (`BoundlessStudy.Service`, `BoundlessStudy.Agent`) should deploy as **native 64-bit (`x64` / `ARM64`)** processes so `System32` paths resolve deterministically without WOW64 redirection.

---

## 4. Non-Windows Platform Fail-Closed Behavior

BPE-1.7 guarantees that non-Windows hosts can never forge, simulate, or bypass OS-verified identity:

| Verifier / Entry Point | Non-Windows Guard | Returned Status & Trust Flags | Exact `ReasonCode` | Verified By |
|---|---|---|---|---|
| `WindowsSessionIdentityVerifier.VerifySessionAsync` | `if (!_isWindows())` ([`WindowsSessionIdentityVerifier.cs#L54-L59`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSessionIdentityVerifier.cs#L54-L59)) | `Status = Unsupported`, `SourceTrust = OperatingSystemVerified`, `Evidence = null`, `IsTrusted = false` | `"platform-unsupported-windows-identity-required"` | [`WindowsSessionIdentityVerifierTests.VerifySessionAsync_WhenNotWindows_ReturnsUnsupported`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/WindowsSessionIdentityVerifierTests.cs#L121-L133) |
| `WindowsProcessIdentityVerifier.VerifyProcess` / `VerifyProcessAsync` | `if (!_isWindows())` ([`WindowsProcessIdentityVerifier.cs#L54-L58`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs#L54-L58)) | `Status = Unsupported`, `SourceTrust = OperatingSystemVerified`, `Evidence = null`, `IsTrusted = false` | `"platform-unsupported-windows-identity-required"` | [`WindowsProcessIdentityVerifierTests.VerifyProcessAsync_WhenNotWindows_ReturnsUnsupported`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/WindowsProcessIdentityVerifierTests.cs#L178-L191) |
| `WindowsExecutableIdentityVerifier.Resolve`, `Verify`, `VerifyAsync` | `if (!OperatingSystem.IsWindows())` ([`WindowsExecutableIdentityVerifier.cs#L96,120,181`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs#L96)) | `Status = Unsupported`, `SourceTrust = OperatingSystemVerified`, `Evidence = null`, `IsTrusted = false` | `"windows-platform-required"` | Code inspection (note: no `isWindows` test-injection hook on `WindowsExecutableIdentityVerifier`) |
| `ProtectionPolicyEvaluator.EvaluateAuthorizedAsync` (internal boundary) | `FromVerificationFailure(Unsupported, ...)` ([`PolicyEvaluation.cs#L363`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/PolicyEvaluation.cs#L363)) | `Decision = Restrict`, `Status = IdentityUnsupported`, `FailClosed = true`, `CanClaimProtection = false` | Propagates verifier reason code | Policy boundary inspection & `EvaluateAuthorizedAsync` tests |

### Compatibility Action Items

1. **Reason-Code Harmonization (Minor):** Align `WindowsExecutableIdentityVerifier`'s non-Windows reason code (`"windows-platform-required"`) with `"platform-unsupported-windows-identity-required"` used by `WindowsSessionIdentityVerifier` and `WindowsProcessIdentityVerifier` if unified telemetry matching is desired.
2. **ARM64 CI Validation (Future):** Add a `windows-11-arm` / ARM64 runner smoke check when BPE advances to native enforcement integration (BPE-1.8+), since local verification covered `win-x64` only.
