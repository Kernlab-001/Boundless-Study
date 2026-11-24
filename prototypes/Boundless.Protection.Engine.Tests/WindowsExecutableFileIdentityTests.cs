using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class WindowsExecutableFileIdentityTests : IDisposable
{
    private readonly string _tempRoot;

    public WindowsExecutableFileIdentityTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bpe-exec-id-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot)) return;
        foreach (string dir in Directory.GetDirectories(_tempRoot, "*", SearchOption.TopDirectoryOnly))
        {
            var attrs = File.GetAttributes(dir);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(dir, recursive: false);
            }
        }
        Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public void ResolveAndVerifyRealWindowsExecutableReturnsAuthoritativeIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string explorerPath = Path.Combine(windowsDir, "explorer.exe");
        Assert.True(File.Exists(explorerPath));

        string expectedSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(explorerPath)));
        var verifier = new WindowsExecutableIdentityVerifier();

        var resolved = verifier.Resolve(explorerPath);

        Assert.True(resolved.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Verified, resolved.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.OperatingSystemVerified, resolved.SourceTrust);
        Assert.NotNull(resolved.Evidence);
        Assert.False(resolved.Evidence!.ExecutablePath.StartsWith(@"\\?\", StringComparison.Ordinal));
        Assert.Equal(explorerPath, resolved.Evidence.ExecutablePath, ignoreCase: true);
        Assert.Equal(expectedSha256, resolved.Evidence.Sha256);
        Assert.NotEqual(0u, resolved.Evidence.VolumeSerialNumber);
        Assert.NotEqual(0UL, resolved.Evidence.FileIndex);
        Assert.True(resolved.Evidence.FileSizeBytes > 0);
        Assert.NotNull(resolved.Evidence.FileId128);
        Assert.Equal(32, resolved.Evidence.FileId128!.Length);

        var verified = verifier.Verify(resolved.Evidence.ExecutablePath, expectedSha256.ToLowerInvariant());
        Assert.True(verified.IsTrusted);
        Assert.Equal("identity-verified", verified.ReasonCode);
    }

    [Fact]
    public async Task VerifyAsyncSucceedsAndHonorsCancellation()
    {
        if (!OperatingSystem.IsWindows()) return;

        string filePath = Path.Combine(_tempRoot, "AsyncTarget.exe");
        byte[] payload = CreateDeterministicPayload(256 * 1024);
        await File.WriteAllBytesAsync(filePath, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        var verifier = new WindowsExecutableIdentityVerifier();
        var canonical = verifier.Resolve(filePath);
        Assert.True(canonical.IsTrusted);

        var verified = await verifier.VerifyAsync(filePath, canonical.Evidence!.ExecutablePath, expectedSha256);
        Assert.True(verified.IsTrusted);
        Assert.Equal(expectedSha256, verified.Evidence!.Sha256);
        Assert.Equal(payload.Length, verified.Evidence.FileSizeBytes);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifyAsync(filePath, canonical.Evidence.ExecutablePath, expectedSha256, cts.Token));
    }

    [Fact]
    public void ResolveNormalizesPathCaseAndRejectsMismatchedOrMalformedPaths()
    {
        if (!OperatingSystem.IsWindows()) return;

        string subDir = Path.Combine(_tempRoot, "MixedCaseFolder");
        Directory.CreateDirectory(subDir);
        string actualPath = Path.Combine(subDir, "BoundlessSampleApp.exe");
        byte[] payload = CreateDeterministicPayload(4096);
        File.WriteAllBytes(actualPath, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        var verifier = new WindowsExecutableIdentityVerifier();
        var baseline = verifier.Resolve(actualPath);
        Assert.True(baseline.IsTrusted);
        string canonicalPath = baseline.Evidence!.ExecutablePath;

        var fromLower = verifier.Resolve(actualPath.ToLowerInvariant());
        Assert.True(fromLower.IsTrusted);
        Assert.Equal(canonicalPath, fromLower.Evidence!.ExecutablePath, StringComparer.Ordinal);

        var fromRelativeTraversal = verifier.Resolve(Path.Combine(subDir, "..", "MixedCaseFolder", "BoundlessSampleApp.exe"));
        Assert.True(fromRelativeTraversal.IsTrusted);
        Assert.Equal(canonicalPath, fromRelativeTraversal.Evidence!.ExecutablePath, StringComparer.Ordinal);

        var mismatchClaim = verifier.Verify(actualPath, Path.Combine(subDir, "DifferentApp.exe"), expectedSha256);
        Assert.False(mismatchClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, mismatchClaim.Status);
        Assert.Equal("executable-path-mismatch", mismatchClaim.ReasonCode);
        Assert.Null(mismatchClaim.Evidence);

        var traversalClaim = verifier.Verify(actualPath, Path.Combine(subDir, "..", "MixedCaseFolder", "BoundlessSampleApp.exe"), expectedSha256);
        Assert.False(traversalClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, traversalClaim.Status);
        Assert.Equal("executable-path-mismatch", traversalClaim.ReasonCode);

        var extendedPrefixClaim = verifier.Verify(actualPath, @"\\?\" + canonicalPath, expectedSha256);
        Assert.False(extendedPrefixClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, extendedPrefixClaim.Status);
        Assert.Equal("executable-path-mismatch", extendedPrefixClaim.ReasonCode);

        var nullByte = verifier.Verify("C:\\app.exe\0bad", canonicalPath, expectedSha256);
        Assert.False(nullByte.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, nullByte.Status);
        Assert.Equal("executable-path-invalid", nullByte.ReasonCode);
    }

    [Fact]
    public void VerifyRejectsSha256Mismatch()
    {
        if (!OperatingSystem.IsWindows()) return;

        string filePath = Path.Combine(_tempRoot, "HashCheck.exe");
        byte[] payload = CreateDeterministicPayload(1024);
        File.WriteAllBytes(filePath, payload);
        string realHash = Convert.ToHexString(SHA256.HashData(payload));
        string wrongHash = (realHash[0] == 'A' ? 'B' : 'A') + realHash[1..];

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalPath = verifier.Resolve(filePath).Evidence!.ExecutablePath;

        var mismatch = verifier.Verify(filePath, canonicalPath, wrongHash);
        Assert.False(mismatch.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, mismatch.Status);
        Assert.Equal("executable-hash-mismatch", mismatch.ReasonCode);
        Assert.Null(mismatch.Evidence);
    }

    [Fact]
    public void ResolveFollowsDirectoryJunctionToCanonicalPathAndSharesFileId()
    {
        if (!OperatingSystem.IsWindows()) return;

        string realDir = Path.Combine(_tempRoot, "RealTargetDirectory");
        string junctionDir = Path.Combine(_tempRoot, "JunctionAliasDirectory");
        Directory.CreateDirectory(realDir);

        string realExe = Path.Combine(realDir, "TargetBinary.exe");
        byte[] payload = CreateDeterministicPayload(8192);
        File.WriteAllBytes(realExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        CreateDirectoryJunction(junctionDir, realDir);
        string junctionExe = Path.Combine(junctionDir, "TargetBinary.exe");

        var verifier = new WindowsExecutableIdentityVerifier();
        var realResolved = verifier.Resolve(realExe);
        var junctionResolved = verifier.Resolve(junctionExe);

        Assert.True(realResolved.IsTrusted);
        Assert.True(junctionResolved.IsTrusted);
        Assert.Equal(realResolved.Evidence!.ExecutablePath, junctionResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Equal(realResolved.Evidence.Sha256, junctionResolved.Evidence.Sha256);
        Assert.Equal(realResolved.Evidence.VolumeSerialNumber, junctionResolved.Evidence.VolumeSerialNumber);
        Assert.Equal(realResolved.Evidence.FileIndex, junctionResolved.Evidence.FileIndex);
        Assert.Equal(realResolved.Evidence.FileId128, junctionResolved.Evidence.FileId128);

        // Claiming the junction alias path itself must fail closed with path mismatch.
        var rejectedAliasClaim = verifier.Verify(junctionExe, junctionExe, expectedSha256);
        Assert.False(rejectedAliasClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedAliasClaim.Status);
        Assert.Equal("executable-path-mismatch", rejectedAliasClaim.ReasonCode);

        // Claiming the resolved canonical path succeeds even when opened via the junction.
        var acceptedCanonicalClaim = verifier.Verify(junctionExe, realResolved.Evidence.ExecutablePath, expectedSha256);
        Assert.True(acceptedCanonicalClaim.IsTrusted);
    }

    [Fact]
    public void ResolveResolvesShortNameWhenEnabledOnVolume()
    {
        if (!OperatingSystem.IsWindows()) return;

        string longDir = Path.Combine(_tempRoot, "LongDirectoryNameForShortPathCheck");
        Directory.CreateDirectory(longDir);
        string longExe = Path.Combine(longDir, "VeryLongExecutableNameForTesting.exe");
        byte[] payload = CreateDeterministicPayload(2048);
        File.WriteAllBytes(longExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalLongPath = verifier.Resolve(longExe).Evidence!.ExecutablePath;

        string? shortPath = TryGetShortPathName(longExe);
        if (string.IsNullOrEmpty(shortPath) || string.Equals(shortPath, longExe, StringComparison.OrdinalIgnoreCase))
        {
            // Fall back to Windows System drive 8.3 alias (e.g. C:\PROGRA~1) if temp volume has 8dot3 disabled.
            string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string? progShort = TryGetShortPathName(programFiles);
            if (string.IsNullOrEmpty(progShort) || string.Equals(progShort, programFiles, StringComparison.OrdinalIgnoreCase))
                return;

            string? candidateExe = Directory.EnumerateFiles(programFiles, "*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                {
                    string? s = TryGetShortPathName(path);
                    return !string.IsNullOrEmpty(s) && s.Contains('~', StringComparison.Ordinal);
                });
            if (candidateExe is null) return;

            string candidateShort = TryGetShortPathName(candidateExe)!;
            var candidateResolved = verifier.Resolve(candidateShort);
            if (!candidateResolved.IsTrusted) return;
            Assert.DoesNotContain("~", candidateResolved.Evidence!.ExecutablePath, StringComparison.Ordinal);
            var rejectedShort = verifier.Verify(candidateShort, candidateShort, candidateResolved.Evidence.Sha256);
            Assert.Equal(IdentityVerificationStatus.Rejected, rejectedShort.Status);
            Assert.Equal("executable-path-mismatch", rejectedShort.ReasonCode);
            return;
        }

        var resolvedFromShort = verifier.Resolve(shortPath);
        Assert.True(resolvedFromShort.IsTrusted);
        Assert.Equal(canonicalLongPath, resolvedFromShort.Evidence!.ExecutablePath, StringComparer.Ordinal);

        var rejectedShortClaim = verifier.Verify(shortPath, shortPath, expectedSha256);
        Assert.False(rejectedShortClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedShortClaim.Status);
        Assert.Equal("executable-path-mismatch", rejectedShortClaim.ReasonCode);

        var acceptedCanonicalClaim = verifier.Verify(shortPath, canonicalLongPath, expectedSha256);
        Assert.True(acceptedCanonicalClaim.IsTrusted);
    }

    [Fact]
    public void HardLinksShareSameVolumeSerialAndFileIndex()
    {
        if (!OperatingSystem.IsWindows()) return;

        string primaryPath = Path.Combine(_tempRoot, "PrimaryBinary.exe");
        string hardLinkPath = Path.Combine(_tempRoot, "HardLinkBinary.exe");
        byte[] payload = CreateDeterministicPayload(4096);
        File.WriteAllBytes(primaryPath, payload);

        Assert.True(CreateHardLinkW(hardLinkPath, primaryPath, IntPtr.Zero),
            $"CreateHardLinkW failed with Win32 error {Marshal.GetLastWin32Error()}.");

        var verifier = new WindowsExecutableIdentityVerifier();
        var primary = verifier.Resolve(primaryPath);
        var linked = verifier.Resolve(hardLinkPath);

        Assert.True(primary.IsTrusted);
        Assert.True(linked.IsTrusted);
        Assert.Equal(primary.Evidence!.Sha256, linked.Evidence!.Sha256);
        Assert.Equal(primary.Evidence.VolumeSerialNumber, linked.Evidence.VolumeSerialNumber);
        Assert.Equal(primary.Evidence.FileIndex, linked.Evidence.FileIndex);
        Assert.Equal(primary.Evidence.FileId128, linked.Evidence.FileId128);
    }

    [Fact]
    public void MissingDirectoryAndLockedFilesFailClosed()
    {
        if (!OperatingSystem.IsWindows()) return;

        var verifier = new WindowsExecutableIdentityVerifier();

        string missingPath = Path.Combine(_tempRoot, "does-not-exist.exe");
        var missing = verifier.Verify(missingPath, missingPath, new string('0', 64));
        Assert.False(missing.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Unavailable, missing.Status);
        Assert.Equal("executable-not-found", missing.ReasonCode);
        Assert.Null(missing.Evidence);

        var directoryResult = verifier.Resolve(_tempRoot);
        Assert.False(directoryResult.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, directoryResult.Status);
        Assert.Equal("executable-is-directory", directoryResult.ReasonCode);

        string lockedPath = Path.Combine(_tempRoot, "LockedForWrite.exe");
        File.WriteAllBytes(lockedPath, CreateDeterministicPayload(512));
        using (var exclusiveWriter = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var sharingViolation = verifier.Resolve(lockedPath);
            Assert.False(sharingViolation.IsTrusted);
            Assert.Equal(IdentityVerificationStatus.Unavailable, sharingViolation.Status);
            Assert.Equal("executable-sharing-violation", sharingViolation.ReasonCode);
        }
    }

    [Fact]
    public void AccessDeniedFileReturnsUnavailableAccessDenied()
    {
        if (!OperatingSystem.IsWindows()) return;

        string restrictedPath = Path.Combine(_tempRoot, "DeniedExecutable.exe");
        File.WriteAllBytes(restrictedPath, CreateDeterministicPayload(512));

        var fileInfo = new FileInfo(restrictedPath);
        SecurityIdentifier? currentUser = WindowsIdentity.GetCurrent().User;
        Assert.NotNull(currentUser);

        var denyRule = new FileSystemAccessRule(
            currentUser!,
            FileSystemRights.ReadData,
            AccessControlType.Deny);

        FileSecurity originalSecurity = fileInfo.GetAccessControl();
        var deniedSecurity = fileInfo.GetAccessControl();
        deniedSecurity.AddAccessRule(denyRule);
        fileInfo.SetAccessControl(deniedSecurity);

        try
        {
            var verifier = new WindowsExecutableIdentityVerifier();
            var result = verifier.Verify(restrictedPath, restrictedPath, new string('a', 64));

            Assert.False(result.IsTrusted);
            Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
            Assert.Equal("executable-access-denied", result.ReasonCode);
            Assert.Null(result.Evidence);
        }
        finally
        {
            originalSecurity.RemoveAccessRule(denyRule);
            fileInfo.SetAccessControl(originalSecurity);
        }
    }

    private static byte[] CreateDeterministicPayload(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 31 + 17) & 0xFF);
        return bytes;
    }

    private static void CreateDirectoryJunction(string junctionPath, string targetPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c mklink /J \"{junctionPath}\" \"{targetPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        Assert.True(process.Start());
        process.WaitForExit(10_000);
        Assert.Equal(0, process.ExitCode);
    }

    private static string? TryGetShortPathName(string longPath)
    {
        var sb = new StringBuilder(512);
        uint result = GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
        if (result == 0) return null;
        if (result >= (uint)sb.Capacity)
        {
            sb = new StringBuilder((int)result + 1);
            result = GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
            if (result == 0 || result >= (uint)sb.Capacity) return null;
        }
        return sb.ToString(0, (int)result);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(
        string lpszLongPath,
        [Out] StringBuilder lpszShortPath,
        uint cchBuffer);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);
}
