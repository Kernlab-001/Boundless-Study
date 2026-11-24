# BPE-1.7 Dependency, License, and Supply-Chain Report

**Audit scope:** `Boundless.Protection.Engine`, `Boundless.Protection.Engine.CrashWorker`, and `Boundless.Protection.Engine.Tests`  
**Audit date:** 2026-11-24  
**Auditor:** Subagent D1 (Security, Compatibility, and Dependency Auditor)  
**Verdict:** **PASS** — Zero new external NuGet packages introduced in BPE-1.7; 0 known vulnerabilities returned by NuGet.org audit; 100% MIT / Apache-2.0 license conformance across all BPE direct and transitive packages.

---

## 1. Zero-New-Dependency Verification for BPE-1.7

Comparison of the project files and working tree (`git status -s` / `git diff`) confirms that **zero external NuGet packages were added** for BPE-1.7:

- [`Boundless.Protection.Engine.csproj`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/Boundless.Protection.Engine.csproj): Retains only its existing `Microsoft.Data.Sqlite` (`10.0.15`) package reference from BPE-1.3. The sole edit in BPE-1.7 is adding `<InternalsVisibleTo Include="Boundless.Protection.Engine.Tests" />` so the test assembly can exercise internal OS probe interfaces and constructors.
- [`Boundless.Protection.Engine.CrashWorker.csproj`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.CrashWorker/Boundless.Protection.Engine.CrashWorker.csproj): Unmodified; references only `Boundless.Protection.Engine.csproj` with zero direct NuGet references.
- [`Boundless.Protection.Engine.Tests.csproj`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.Tests/Boundless.Protection.Engine.Tests.csproj): Unmodified; retains only the four existing test-runner/coverage packages (`coverlet.collector`, `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`).

All Windows identity verification APIs in [`WindowsSecurityApis.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSecurityApis.cs), [`WindowsSessionIdentityVerifier.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsSessionIdentityVerifier.cs), [`WindowsProcessIdentityVerifier.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsProcessIdentityVerifier.cs), [`WindowsExecutableIdentityVerifier.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine/WindowsExecutableIdentityVerifier.cs), and [`Program.cs`](file:///E:/Booundless%20Study/bpe13-work/prototypes/Boundless.Protection.Engine.CrashWorker/Program.cs) are implemented in native C# using `.NET 10` BCL primitives (`System.Security.Cryptography.SHA256`, `Microsoft.Win32.SafeHandles`, `System.Runtime.InteropServices`) and direct Win32 `DllImport` bindings against inbox Windows system DLLs (`advapi32.dll`, `kernel32.dll`). No third-party P/Invoke wrapper packages (such as `CsWin32`, `Vanara`, or `PInvoke.*`) were added.

---

## 2. Package Inventory & License Conformance

Inspected via `dotnet list <project> package --include-transitive` and verified against each package's `.nuspec` metadata in the local NuGet cache (`%USERPROFILE%\.nuget\packages`).

### 2.1 `Boundless.Protection.Engine` (`net10.0`)

| Dependency Type | Package ID | Resolved Version | License | Source / `.nuspec` Verification |
|---|---|---:|---|---|
| Direct | `Microsoft.Data.Sqlite` | 10.0.15 | MIT | `<license type="expression">MIT</license>` |
| Transitive | `Microsoft.Data.Sqlite.Core` | 10.0.15 | MIT | `<license type="expression">MIT</license>` |
| Transitive | `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.13 | Apache-2.0 | `<license type="expression">Apache-2.0</license>` |
| Transitive | `SQLitePCLRaw.core` | 2.1.13 | Apache-2.0 | `<license type="expression">Apache-2.0</license>` |
| Transitive | `SQLitePCLRaw.lib.e_sqlite3` | 2.1.13 | Apache-2.0 | `<license type="expression">Apache-2.0</license>` |
| Transitive | `SQLitePCLRaw.provider.e_sqlite3` | 2.1.13 | Apache-2.0 | `<license type="expression">Apache-2.0</license>` |

### 2.2 `Boundless.Protection.Engine.CrashWorker` (`net10.0`)

`Boundless.Protection.Engine.CrashWorker` declares **0 direct NuGet packages** and inherits the exact 6-package SQLite closure from `Boundless.Protection.Engine` transitively (`Microsoft.Data.Sqlite` 10.0.15, `Microsoft.Data.Sqlite.Core` 10.0.15, and the four `SQLitePCLRaw.*` 2.1.13 packages — all MIT or Apache-2.0).

### 2.3 `Boundless.Protection.Engine.Tests` (`net10.0`, test-only project)

| Dependency Type | Package ID | Resolved Version | License | Notes |
|---|---|---:|---|---|
| Direct | `coverlet.collector` | 6.0.4 | MIT | Code coverage collector (non-shipping) |
| Direct | `Microsoft.NET.Test.Sdk` | 17.14.1 | MIT | Test host SDK (non-shipping) |
| Direct | `xunit` | 2.9.3 | Apache-2.0 | Test framework (non-shipping) |
| Direct | `xunit.runner.visualstudio` | 3.1.4 | Apache-2.0 | Test adapter (non-shipping) |
| Transitive | `Microsoft.CodeCoverage` | 17.14.1 | MIT | Via `Microsoft.NET.Test.Sdk` |
| Transitive | `Microsoft.Data.Sqlite` | 10.0.15 | MIT | Via `Boundless.Protection.Engine` |
| Transitive | `Microsoft.Data.Sqlite.Core` | 10.0.15 | MIT | Via `Microsoft.Data.Sqlite` |
| Transitive | `Microsoft.TestPlatform.ObjectModel` | 17.14.1 | MIT | Via `Microsoft.NET.Test.Sdk` |
| Transitive | `Microsoft.TestPlatform.TestHost` | 17.14.1 | MIT | Via `Microsoft.NET.Test.Sdk` |
| Transitive | `Newtonsoft.Json` | 13.0.3 | MIT | Via `Microsoft.TestPlatform.*` (test-only) |
| Transitive | `SQLitePCLRaw.bundle_e_sqlite3` | 2.1.13 | Apache-2.0 | Via `Microsoft.Data.Sqlite` |
| Transitive | `SQLitePCLRaw.core` | 2.1.13 | Apache-2.0 | Via `Microsoft.Data.Sqlite` |
| Transitive | `SQLitePCLRaw.lib.e_sqlite3` | 2.1.13 | Apache-2.0 | Via `SQLitePCLRaw.bundle_e_sqlite3` |
| Transitive | `SQLitePCLRaw.provider.e_sqlite3` | 2.1.13 | Apache-2.0 | Via `SQLitePCLRaw.bundle_e_sqlite3` |
| Transitive | `xunit.abstractions` | 2.0.3 | Apache-2.0 (legacy `licenseUrl`) | Legacy `.nuspec` references `https://raw.githubusercontent.com/xunit/xunit/master/license.txt` (xUnit.net Apache-2.0) |
| Transitive | `xunit.analyzers` | 1.18.0 | Apache-2.0 | Via `xunit` |
| Transitive | `xunit.assert` | 2.9.3 | Apache-2.0 | Via `xunit` |
| Transitive | `xunit.core` | 2.9.3 | Apache-2.0 | Via `xunit` |
| Transitive | `xunit.extensibility.core` | 2.9.3 | Apache-2.0 | Via `xunit` |
| Transitive | `xunit.extensibility.execution` | 2.9.3 | Apache-2.0 | Via `xunit` |

**License Conformance Summary:** All 20 unique package/version entries across the three BPE projects conform strictly to permissive **MIT** or **Apache-2.0** licenses. Zero GPL, LGPL, AGPL, SSPL, or commercial/proprietary NuGet packages are present.

---

## 3. Vulnerability Audit Results

Executed on 2026-11-24 against `https://api.nuget.org/v3/index.json` (NuGet advisory feeds `vulnerability.base.json` and `vulnerability.update.json` active; NuGet auditing enabled):

```text
dotnet list "...\Boundless.Protection.Engine.csproj" package --vulnerable --include-transitive
dotnet list "...\Boundless.Protection.Engine.CrashWorker.csproj" package --vulnerable --include-transitive
dotnet list "...\Boundless.Protection.Engine.Tests.csproj" package --vulnerable --include-transitive
```

| Project | Vulnerable Direct Packages | Vulnerable Transitive Packages | Status |
|---|---:|---:|---|
| `Boundless.Protection.Engine` | 0 | 0 | **PASS** |
| `Boundless.Protection.Engine.CrashWorker` | 0 | 0 | **PASS** |
| `Boundless.Protection.Engine.Tests` | 0 | 0 | **PASS** |

*Note:* As with all advisory-feed audits, zero reported advisories reflect known NuGet.org vulnerability data as of 2026-11-24 and do not guarantee absence of future CVEs; recurring scans remain configured via [`.github/workflows/dependency-audit.yml`](file:///E:/Booundless%20Study/bpe13-work/.github/workflows/dependency-audit.yml) and [`.github/dependabot.yml`](file:///E:/Booundless%20Study/bpe13-work/.github/dependabot.yml).

---

## 4. Reaffirmation of BPE-1.6.3 Encryption Resolution

Consistent with [BPE-1.6.3-Encryption-Resolution.md](file:///E:/Booundless%20Study/bpe13-work/deliverables/BPE-1.6.3-Encryption-Resolution.md):

1. **Approved Encryption Architecture Remains Unchanged:** Page-level SQLite encryption must use **SQLCipher for .NET (commercial/enterprise package)** integrated strictly via `Microsoft.Data.Sqlite.Core` (removing `SQLitePCLRaw.bundle_e_sqlite3` from production encrypted builds), paired with a 256-bit CSPRNG database key protected by Windows DPAPI `CurrentUser` scope (`ProtectedData`) in a versioned key envelope.
2. **SQLCipher Implementation Remains Blocked:** No Zetetic SQLCipher commercial license or private vendor NuGet feed is present in the repository, and disposable Windows 11 VM validation of DPAPI `CurrentUser` behavior (Gates 1–6 of BPE-1.6.3) has not been completed.
3. **Permissive Alternates Remain Rejected:** Plaintext `SQLitePCLRaw.bundle_e_sqlite3` ignores `Password` connection strings; unsupported third-party `e_sqlcipher` bundles and `LiteDB` encryption remain rejected.
4. **Operational Consequence:** `SqliteProtectionJournal` and `PersistentSimulatedPolicyProvider` remain synthetic-only prototypes; no production user identities, SIDs, LUIDs, or enforcement policies may be persisted to SQLite until the SQLCipher commercial feed/license and BPE-1.6.3 gates are satisfied.
