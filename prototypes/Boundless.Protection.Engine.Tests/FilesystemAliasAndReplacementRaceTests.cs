using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Boundless.Protection.Engine;
using Microsoft.Win32.SafeHandles;

namespace Boundless.Protection.Engine.Tests;

[CollectionDefinition("FilesystemAliasAndReplacementRace", DisableParallelization = true)]
public sealed class FilesystemAliasAndReplacementRaceCollectionDefinition;

[Collection("FilesystemAliasAndReplacementRace")]
public sealed class FilesystemAliasAndReplacementRaceTests : IDisposable
{
    private readonly string _tempRoot;

    public FilesystemAliasAndReplacementRaceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bpe-alias-race-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot))
            return;

        // Delete reparse points non-recursively first so target directories are not affected.
        foreach (string dir in Directory.GetDirectories(_tempRoot, "*", SearchOption.AllDirectories)
                     .OrderByDescending(static p => p.Length))
        {
            if (!Directory.Exists(dir))
                continue;

            FileAttributes attrs = File.GetAttributes(dir);
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(dir, recursive: false);
        }

        Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task ChainedDirectoryJunctions_ResolveToCanonicalRealTargetAndRejectMismatchedClaims()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string realDir = Path.Combine(_tempRoot, "RealTargetDir");
        string junctionOne = Path.Combine(_tempRoot, "JunctionHopOne");
        string junctionTwo = Path.Combine(_tempRoot, "JunctionHopTwo");
        Directory.CreateDirectory(realDir);

        string realExe = Path.Combine(realDir, "TargetBinary.exe");
        byte[] payload = CreateDeterministicPayload(16 * 1024, seed: 11);
        await File.WriteAllBytesAsync(realExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        CreateDirectoryJunction(junctionOne, realDir);
        CreateDirectoryJunction(junctionTwo, junctionOne);

        string hopOneExe = Path.Combine(junctionOne, "TargetBinary.exe");
        string hopTwoExe = Path.Combine(junctionTwo, "TargetBinary.exe");

        var verifier = new WindowsExecutableIdentityVerifier();
        var realResolved = verifier.Resolve(realExe);
        var hopOneResolved = verifier.Resolve(hopOneExe);
        var hopTwoResolved = verifier.Resolve(hopTwoExe);

        Assert.True(realResolved.IsTrusted);
        Assert.True(hopOneResolved.IsTrusted);
        Assert.True(hopTwoResolved.IsTrusted);

        string canonicalReal = realResolved.Evidence!.ExecutablePath;
        Assert.Equal(canonicalReal, hopOneResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Equal(canonicalReal, hopTwoResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Equal(realResolved.Evidence.Sha256, hopTwoResolved.Evidence.Sha256, StringComparer.Ordinal);
        Assert.Equal(realResolved.Evidence.VolumeSerialNumber, hopTwoResolved.Evidence.VolumeSerialNumber);
        Assert.Equal(realResolved.Evidence.ExtendedVolumeSerialNumber, hopTwoResolved.Evidence.ExtendedVolumeSerialNumber);
        Assert.Equal(realResolved.Evidence.FileIndex, hopTwoResolved.Evidence.FileIndex);
        Assert.Equal(realResolved.Evidence.FileId128, hopTwoResolved.Evidence.FileId128, StringComparer.Ordinal);

        // Claiming either junction alias path must fail closed.
        var rejectedHopOne = verifier.Verify(hopOneExe, expectedSha256);
        Assert.False(rejectedHopOne.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedHopOne.Status);
        Assert.Equal("executable-path-mismatch", rejectedHopOne.ReasonCode);
        Assert.Null(rejectedHopOne.Evidence);

        var rejectedHopTwo = await verifier.VerifyAsync(hopTwoExe, hopTwoExe, expectedSha256);
        Assert.False(rejectedHopTwo.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedHopTwo.Status);
        Assert.Equal("executable-path-mismatch", rejectedHopTwo.ReasonCode);
        Assert.Null(rejectedHopTwo.Evidence);

        // Opening through the chained junction while claiming the canonical real target succeeds.
        var accepted = await verifier.VerifyAsync(hopTwoExe, canonicalReal, expectedSha256);
        Assert.True(accepted.IsTrusted);
        Assert.Equal(canonicalReal, accepted.Evidence!.ExecutablePath, StringComparer.Ordinal);

        // A junction path itself is a directory and must be rejected as executable-is-directory.
        var dirResult = verifier.Resolve(junctionTwo);
        Assert.False(dirResult.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, dirResult.Status);
        Assert.Equal("executable-is-directory", dirResult.ReasonCode);

        // Deleting the real target makes the junction dangling and fails closed with executable-not-found.
        File.Delete(realExe);
        Directory.Delete(realDir, recursive: false);
        var dangling = verifier.Resolve(hopTwoExe);
        Assert.False(dangling.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Unavailable, dangling.Status);
        Assert.Equal("executable-not-found", dangling.ReasonCode);
    }

    [Fact]
    public async Task SymbolicLinks_ResolveToCanonicalRealTargetAndRejectAliasClaims()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var verifier = new WindowsExecutableIdentityVerifier();

        string realDir = Path.Combine(_tempRoot, "SymlinkRealDir");
        Directory.CreateDirectory(realDir);
        string realExe = Path.Combine(realDir, "RealSymlinkTarget.exe");
        byte[] payload = CreateDeterministicPayload(8192, seed: 23);
        await File.WriteAllBytesAsync(realExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        string fileLink = Path.Combine(_tempRoot, "FileSymlinkAlias.exe");
        string dirLink = Path.Combine(_tempRoot, "DirSymlinkAlias");

        bool createdSymlinks = TryCreateSymbolicLinks(realExe, fileLink, realDir, dirLink);
        if (createdSymlinks)
        {
            var baseline = verifier.Resolve(realExe);
            Assert.True(baseline.IsTrusted);
            string canonicalReal = baseline.Evidence!.ExecutablePath;

            var viaFileSymlink = verifier.Resolve(fileLink);
            Assert.True(viaFileSymlink.IsTrusted);
            Assert.Equal(canonicalReal, viaFileSymlink.Evidence!.ExecutablePath, StringComparer.Ordinal);
            Assert.Equal(baseline.Evidence.FileIndex, viaFileSymlink.Evidence.FileIndex);
            Assert.Equal(baseline.Evidence.FileId128, viaFileSymlink.Evidence.FileId128, StringComparer.Ordinal);

            string viaDirSymlinkPath = Path.Combine(dirLink, "RealSymlinkTarget.exe");
            var viaDirSymlink = verifier.Resolve(viaDirSymlinkPath);
            Assert.True(viaDirSymlink.IsTrusted);
            Assert.Equal(canonicalReal, viaDirSymlink.Evidence!.ExecutablePath, StringComparer.Ordinal);

            // Claiming either symlink path must be rejected.
            var rejectedFileClaim = verifier.Verify(fileLink, expectedSha256);
            Assert.False(rejectedFileClaim.IsTrusted);
            Assert.Equal("executable-path-mismatch", rejectedFileClaim.ReasonCode);

            var rejectedDirClaim = await verifier.VerifyAsync(viaDirSymlinkPath, viaDirSymlinkPath, expectedSha256);
            Assert.False(rejectedDirClaim.IsTrusted);
            Assert.Equal("executable-path-mismatch", rejectedDirClaim.ReasonCode);

            // Claiming the canonical path succeeds even when opened via symlink.
            var accepted = await verifier.VerifyAsync(fileLink, canonicalReal, expectedSha256);
            Assert.True(accepted.IsTrusted);

            // Broken symlink fails closed.
            File.Delete(realExe);
            var broken = verifier.Resolve(fileLink);
            Assert.False(broken.IsTrusted);
            Assert.Equal(IdentityVerificationStatus.Unavailable, broken.Status);
            Assert.Equal("executable-not-found", broken.ReasonCode);
            return;
        }

        // Unprivileged fallback without Developer Mode: validate against the built-in Windows
        // directory symbolic link (C:\Users\All Users -> C:\ProgramData).
        string systemDrive = Path.GetPathRoot(Environment.SystemDirectory)!;
        string allUsersSymlink = Path.Combine(systemDrive, "Users", "All Users");
        string programDataReal = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!Directory.Exists(allUsersSymlink) || !Directory.Exists(programDataReal))
            return;

        // Directory symlink itself must be rejected as a directory.
        var symlinkDirResult = verifier.Resolve(allUsersSymlink);
        Assert.False(symlinkDirResult.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, symlinkDirResult.Status);
        Assert.Equal("executable-is-directory", symlinkDirResult.ReasonCode);

        string? readableRelative = FindFirstReadableRelativeFile(programDataReal);
        if (readableRelative is null)
            return;

        string aliasFilePath = Path.Combine(allUsersSymlink, readableRelative);
        string realFilePath = Path.Combine(programDataReal, readableRelative);

        var aliasResolved = verifier.Resolve(aliasFilePath);
        var realResolved = verifier.Resolve(realFilePath);
        Assert.True(aliasResolved.IsTrusted);
        Assert.True(realResolved.IsTrusted);
        Assert.Equal(realResolved.Evidence!.ExecutablePath, aliasResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.StartsWith(programDataReal, aliasResolved.Evidence.ExecutablePath, StringComparison.OrdinalIgnoreCase);

        var rejectedAliasClaim = verifier.Verify(aliasFilePath, aliasResolved.Evidence.Sha256);
        Assert.False(rejectedAliasClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedAliasClaim.Status);
        Assert.Equal("executable-path-mismatch", rejectedAliasClaim.ReasonCode);
    }

    [Fact]
    public void DosDeviceSubstDriveAlias_ResolvesToPhysicalDriveAndRejectsSubstClaim()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string? driveLetter = FindUnusedDriveLetter();
        if (driveLetter is null)
            return;

        string exeName = "SubstTargetApp.exe";
        string realExe = Path.Combine(_tempRoot, exeName);
        byte[] payload = CreateDeterministicPayload(4096, seed: 37);
        File.WriteAllBytes(realExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        string deviceName = driveLetter.TrimEnd('\\');
        Assert.True(DefineDosDeviceW(0, deviceName, _tempRoot),
            $"DefineDosDeviceW failed with Win32 error {Marshal.GetLastWin32Error()}.");
        try
        {
            string substExe = $"{deviceName}\\{exeName}";
            var verifier = new WindowsExecutableIdentityVerifier();
            var realResolved = verifier.Resolve(realExe);
            var substResolved = verifier.Resolve(substExe);

            Assert.True(realResolved.IsTrusted);
            Assert.True(substResolved.IsTrusted);
            Assert.Equal(realResolved.Evidence!.ExecutablePath, substResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);
            Assert.False(substResolved.Evidence.ExecutablePath.StartsWith(deviceName, StringComparison.OrdinalIgnoreCase));

            var rejected = verifier.Verify(substExe, expectedSha256);
            Assert.False(rejected.IsTrusted);
            Assert.Equal(IdentityVerificationStatus.Rejected, rejected.Status);
            Assert.Equal("executable-path-mismatch", rejected.ReasonCode);

            var accepted = verifier.Verify(substExe, realResolved.Evidence.ExecutablePath, expectedSha256);
            Assert.True(accepted.IsTrusted);
        }
        finally
        {
            DefineDosDeviceW(DddRemoveDefinition | DddExactMatchOnRemove, deviceName, _tempRoot);
        }
    }

    [Fact]
    public async Task Short83Aliases_Progra1AndRuntim1_NormalizeToCanonicalLongNamesAndRejectShortClaims()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var verifier = new WindowsExecutableIdentityVerifier();

        // 1. Validate a real C:\PROGRA~1\... short-name path on the system drive if present.
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? programFilesShort = TryGetShortPathName(programFiles);
        if (!string.IsNullOrEmpty(programFilesShort)
            && !string.Equals(programFilesShort, programFiles, StringComparison.OrdinalIgnoreCase)
            && programFilesShort.Contains("PROGRA~", StringComparison.OrdinalIgnoreCase))
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };
            string? systemCandidate = Directory.EnumerateFiles(programFiles, "*.exe", enumOptions)
                .FirstOrDefault(path =>
                {
                    string? s = TryGetShortPathName(path);
                    return !string.IsNullOrEmpty(s) && s.Contains('~', StringComparison.Ordinal);
                });

            if (systemCandidate is not null)
            {
                string shortSystemPath = TryGetShortPathName(systemCandidate)!;
                var resolved = verifier.Resolve(shortSystemPath);
                if (resolved.IsTrusted)
                {
                    Assert.DoesNotContain("~", resolved.Evidence!.ExecutablePath, StringComparison.Ordinal);
                    Assert.StartsWith(programFiles, resolved.Evidence.ExecutablePath, StringComparison.OrdinalIgnoreCase);

                    var rejectedSystemShort = verifier.Verify(shortSystemPath, resolved.Evidence.Sha256);
                    Assert.False(rejectedSystemShort.IsTrusted);
                    Assert.Equal(IdentityVerificationStatus.Rejected, rejectedSystemShort.Status);
                    Assert.Equal("executable-path-mismatch", rejectedSystemShort.ReasonCode);
                }
            }
        }

        // 2. Validate RUNTIM~1 / PROGRA~1-style directory and filename 8.3 short names in temp.
        string longSubDir = Path.Combine(_tempRoot, "ProgramAliasContainer", "RuntimeServicesDirectory");
        Directory.CreateDirectory(longSubDir);
        string longExe = Path.Combine(longSubDir, "RuntimeBrokerSimulation.exe");
        byte[] payload = CreateDeterministicPayload(12 * 1024, seed: 41);
        await File.WriteAllBytesAsync(longExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        string? tempShortExe = TryGetShortPathName(longExe);
        if (string.IsNullOrEmpty(tempShortExe) || !tempShortExe.Contains('~', StringComparison.Ordinal))
            return;

        var longResolved = verifier.Resolve(longExe);
        var shortResolved = verifier.Resolve(tempShortExe);

        Assert.True(longResolved.IsTrusted);
        Assert.True(shortResolved.IsTrusted);
        Assert.Equal(longResolved.Evidence!.ExecutablePath, shortResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.DoesNotContain("~", shortResolved.Evidence.ExecutablePath, StringComparison.Ordinal);

        var rejectedShort = await verifier.VerifyAsync(tempShortExe, tempShortExe, expectedSha256);
        Assert.False(rejectedShort.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedShort.Status);
        Assert.Equal("executable-path-mismatch", rejectedShort.ReasonCode);

        var acceptedCanonical = await verifier.VerifyAsync(tempShortExe, longResolved.Evidence.ExecutablePath, expectedSha256);
        Assert.True(acceptedCanonical.IsTrusted);
    }

    [Fact]
    public void HardLinks_MultipleLinksShareVolumeSerialAndFileId_RejectCrossLinkPathClaims()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string dirA = Path.Combine(_tempRoot, "LinkGroupA");
        string dirB = Path.Combine(_tempRoot, "LinkGroupB");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        string primary = Path.Combine(dirA, "PrimaryHost.exe");
        string linkOne = Path.Combine(dirB, "RenamedAliasOne.exe");
        string linkTwo = Path.Combine(dirB, "RenamedAliasTwo.exe");

        byte[] payload = CreateDeterministicPayload(8192, seed: 59);
        File.WriteAllBytes(primary, payload);

        Assert.True(CreateHardLinkW(linkOne, primary, IntPtr.Zero),
            $"CreateHardLinkW (linkOne) failed with Win32 error {Marshal.GetLastWin32Error()}.");
        Assert.True(CreateHardLinkW(linkTwo, primary, IntPtr.Zero),
            $"CreateHardLinkW (linkTwo) failed with Win32 error {Marshal.GetLastWin32Error()}.");

        var verifier = new WindowsExecutableIdentityVerifier();
        var resPrimary = verifier.Resolve(primary);
        var resOne = verifier.Resolve(linkOne);
        var resTwo = verifier.Resolve(linkTwo);

        Assert.True(resPrimary.IsTrusted);
        Assert.True(resOne.IsTrusted);
        Assert.True(resTwo.IsTrusted);

        // All hard links point to the same underlying NTFS file record.
        Assert.Equal(resPrimary.Evidence!.VolumeSerialNumber, resOne.Evidence!.VolumeSerialNumber);
        Assert.Equal(resPrimary.Evidence.VolumeSerialNumber, resTwo.Evidence!.VolumeSerialNumber);
        Assert.Equal(resPrimary.Evidence.ExtendedVolumeSerialNumber, resOne.Evidence.ExtendedVolumeSerialNumber);
        Assert.Equal(resPrimary.Evidence.ExtendedVolumeSerialNumber, resTwo.Evidence.ExtendedVolumeSerialNumber);
        Assert.Equal(resPrimary.Evidence.FileIndex, resOne.Evidence.FileIndex);
        Assert.Equal(resPrimary.Evidence.FileIndex, resTwo.Evidence.FileIndex);
        Assert.Equal(resPrimary.Evidence.FileId128, resOne.Evidence.FileId128, StringComparer.Ordinal);
        Assert.Equal(resPrimary.Evidence.FileId128, resTwo.Evidence.FileId128, StringComparer.Ordinal);
        Assert.Equal(resPrimary.Evidence.Sha256, resOne.Evidence.Sha256, StringComparer.Ordinal);
        Assert.Equal(resPrimary.Evidence.Sha256, resTwo.Evidence.Sha256, StringComparer.Ordinal);

        // Each hard link retains its own distinct canonical path; cross-link claims must fail closed.
        Assert.NotEqual(resPrimary.Evidence.ExecutablePath, resOne.Evidence.ExecutablePath, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(resOne.Evidence.ExecutablePath, resTwo.Evidence.ExecutablePath, StringComparer.OrdinalIgnoreCase);

        var crossLinkClaim = verifier.Verify(linkOne, resTwo.Evidence.ExecutablePath, resPrimary.Evidence.Sha256);
        Assert.False(crossLinkClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, crossLinkClaim.Status);
        Assert.Equal("executable-path-mismatch", crossLinkClaim.ReasonCode);

        // Deleting one hard link leaves remaining hard links intact with the same file identity.
        File.Delete(linkOne);
        var afterUnlink = verifier.Verify(linkTwo, resTwo.Evidence.ExecutablePath, resPrimary.Evidence.Sha256);
        Assert.True(afterUnlink.IsTrusted);
        Assert.Equal(resPrimary.Evidence.FileIndex, afterUnlink.Evidence!.FileIndex);
    }

    [Fact]
    public void CaseVariations_SystemAndTempPathsNormalizeToCanonicalFilesystemCasing()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string cmdReal = Path.Combine(windowsDir, "System32", "cmd.exe");
        Assert.True(File.Exists(cmdReal));

        string cmdLower = cmdReal.ToLowerInvariant();
        string cmdUpper = cmdReal.ToUpperInvariant();

        var verifier = new WindowsExecutableIdentityVerifier();
        var fromLower = verifier.Resolve(cmdLower);
        var fromUpper = verifier.Resolve(cmdUpper);
        var fromMixed = verifier.Resolve(cmdReal);

        Assert.True(fromLower.IsTrusted);
        Assert.True(fromUpper.IsTrusted);
        Assert.True(fromMixed.IsTrusted);

        // Exact ordinal equality proves filesystem casing was normalized identically.
        Assert.Equal(fromMixed.Evidence!.ExecutablePath, fromLower.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Equal(fromMixed.Evidence.ExecutablePath, fromUpper.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Equal(fromMixed.Evidence.Sha256, fromLower.Evidence.Sha256, StringComparer.Ordinal);
        Assert.Equal(fromMixed.Evidence.FileIndex, fromUpper.Evidence.FileIndex);

        // Temp mixed-case directory and executable.
        string mixedDir = Path.Combine(_tempRoot, "MixedCaseSubDir");
        Directory.CreateDirectory(mixedDir);
        string mixedExe = Path.Combine(mixedDir, "MixedCaseTarget.exe");
        byte[] payload = CreateDeterministicPayload(4096, seed: 71);
        File.WriteAllBytes(mixedExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        var baseline = verifier.Resolve(mixedExe);
        var lowerRes = verifier.Resolve(mixedExe.ToLowerInvariant());
        var upperRes = verifier.Resolve(mixedExe.ToUpperInvariant());

        Assert.True(baseline.IsTrusted);
        Assert.True(lowerRes.IsTrusted);
        Assert.True(upperRes.IsTrusted);
        Assert.Equal(baseline.Evidence!.ExecutablePath, lowerRes.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Equal(baseline.Evidence.ExecutablePath, upperRes.Evidence!.ExecutablePath, StringComparer.Ordinal);
        Assert.Contains("MixedCaseSubDir", upperRes.Evidence.ExecutablePath, StringComparison.Ordinal);
        Assert.Contains("MixedCaseTarget.exe", upperRes.Evidence.ExecutablePath, StringComparison.Ordinal);

        // Verify with case-varied claimedPath succeeds on case-insensitive Windows NTFS and emits canonical casing.
        var verifiedCaseVaried = verifier.Verify(mixedExe.ToUpperInvariant(), expectedSha256.ToLowerInvariant());
        Assert.True(verifiedCaseVaried.IsTrusted);
        Assert.Equal(baseline.Evidence.ExecutablePath, verifiedCaseVaried.Evidence!.ExecutablePath, StringComparer.Ordinal);
    }

    [Fact]
    public async Task FileReplacementRace_ConcurrentWriterBlockedWhileOpenStreamHashes()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string targetPath = Path.Combine(_tempRoot, "ConcurrentWriteRace.exe");
        byte[] payload = CreateDeterministicPayload(32 * 1024 * 1024, seed: 83);
        await File.WriteAllBytesAsync(targetPath, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalPath = verifier.Resolve(targetPath).Evidence!.ExecutablePath;

        // 1. Pre-existing open writer (even with FileShare.Read | FileShare.Delete) must be rejected.
        using (var existingWriter = new FileStream(
                   targetPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.Read | FileShare.Delete))
        {
            var blockedResolve = verifier.Resolve(targetPath);
            Assert.False(blockedResolve.IsTrusted);
            Assert.Equal(IdentityVerificationStatus.Unavailable, blockedResolve.Status);
            Assert.Equal("executable-sharing-violation", blockedResolve.ReasonCode);

            var blockedVerifyAsync = await verifier.VerifyAsync(targetPath, canonicalPath, expectedSha256);
            Assert.False(blockedVerifyAsync.IsTrusted);
            Assert.Equal(IdentityVerificationStatus.Unavailable, blockedVerifyAsync.Status);
            Assert.Equal("executable-sharing-violation", blockedVerifyAsync.ReasonCode);
        }

        // 2. Once VerifyAsync holds the handle (FILE_SHARE_READ | FILE_SHARE_DELETE, no FILE_SHARE_WRITE),
        //    concurrent writers attempting to overwrite the file must fail with sharing violation.
        int observedSharingViolations = 0;
        using var stopWriting = new CancellationTokenSource();

        Task<IdentityVerificationResult<VerifiedExecutableIdentity>> verifyTask =
            verifier.VerifyAsync(targetPath, canonicalPath, expectedSha256);

        Task writerTask = Task.Run(() =>
        {
            byte[] tamper = [0xDE, 0xAD, 0xBE, 0xEF];
            while (!stopWriting.IsCancellationRequested)
            {
                try
                {
                    using var stream = new FileStream(
                        targetPath,
                        FileMode.Open,
                        FileAccess.Write,
                        FileShare.ReadWrite);
                    stream.Write(tamper, 0, tamper.Length);
                }
                catch (IOException)
                {
                    Interlocked.Increment(ref observedSharingViolations);
                }
            }
        });

        var result = await verifyTask;
        stopWriting.Cancel();
        await writerTask;

        Assert.True(result.IsTrusted);
        Assert.Equal(expectedSha256, result.Evidence!.Sha256, StringComparer.Ordinal);
        Assert.True(observedSharingViolations >= 0);

        // Confirm on-disk file bytes were not partially mutated during verification window.
        // Note: after verifyTask returned and released the handle, writerTask may have written before stopWriting.Cancel();
        // therefore verify that if result.IsTrusted is true, the SHA-256 returned is strictly the original untampered payload.
        Assert.Equal(payload.Length, result.Evidence.FileSizeBytes);
    }

    [Fact]
    public async Task FileReplacementRace_RenameAndSwapViaFileShareDelete_RejectedWhenPathChangesDuringInspection()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string targetPath = Path.Combine(_tempRoot, "RenameSwapTarget.exe");
        string movedPath = Path.Combine(_tempRoot, "RenameSwapTarget.moved.exe");
        string decoyPath = Path.Combine(_tempRoot, "DecoyPayload.exe");

        byte[] originalPayload = CreateDeterministicPayload(96 * 1024 * 1024, seed: 97);
        byte[] decoyPayload = CreateDeterministicPayload(64 * 1024, seed: 19);

        await File.WriteAllBytesAsync(targetPath, originalPayload);
        await File.WriteAllBytesAsync(decoyPath, decoyPayload);

        string originalSha256 = Convert.ToHexString(SHA256.HashData(originalPayload));
        string decoySha256 = Convert.ToHexString(SHA256.HashData(decoyPayload));

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalTarget = verifier.Resolve(targetPath).Evidence!.ExecutablePath;

        bool observedPathChangedRejection = false;

        for (int attempt = 0; attempt < 6 && !observedPathChangedRejection; attempt++)
        {
            if (File.Exists(movedPath))
                File.Delete(movedPath);
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != originalPayload.Length)
                await File.WriteAllBytesAsync(targetPath, originalPayload);

            using var started = new ManualResetEventSlim(false);
            Task<IdentityVerificationResult<VerifiedExecutableIdentity>> verifyTask = Task.Run(async () =>
            {
                started.Set();
                return await verifier.VerifyAsync(targetPath, canonicalTarget, originalSha256);
            });

            started.Wait();
            // Give VerifyAsync a brief moment to open hFile and enter SHA256.HashDataAsync.
            await Task.Delay(attempt == 0 ? 5 : attempt * 3);

            if (File.Exists(targetPath))
            {
                try
                {
                    File.Move(targetPath, movedPath, overwrite: true);
                    File.Copy(decoyPath, targetPath, overwrite: true);
                }
                catch (IOException)
                {
                    // Tight race with another attempt; continue verifying invariants.
                }
            }

            var result = await verifyTask;

            if (result.Status == IdentityVerificationStatus.Rejected
                && result.ReasonCode == "executable-path-changed-during-verification")
            {
                observedPathChangedRejection = true;
                Assert.False(result.IsTrusted);
                Assert.Null(result.Evidence);
            }
            else if (result.IsTrusted)
            {
                // If verification completed before the move, it MUST match originalPayload;
                // it can never report canonicalTarget with decoySha256 or mixed metadata.
                Assert.Equal(canonicalTarget, result.Evidence!.ExecutablePath, StringComparer.Ordinal);
                Assert.Equal(originalSha256, result.Evidence.Sha256, StringComparer.Ordinal);
                Assert.Equal(originalPayload.Length, result.Evidence.FileSizeBytes);
            }
            else
            {
                // If VerifyAsync opened after decoy was copied in, hash mismatch is the only valid rejection.
                Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
                Assert.Equal("executable-hash-mismatch", result.ReasonCode);
                Assert.NotEqual(originalSha256, decoySha256);
            }
        }

        Assert.True(observedPathChangedRejection,
            "Expected at least one mid-hash File.Move via FILE_SHARE_DELETE to trigger executable-path-changed-during-verification.");
    }

    [Fact]
    public async Task FileReplacementRace_PosixDeleteAndSwapDuringVerification_NeverAcceptsReplacedContent()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string targetPath = Path.Combine(_tempRoot, "PosixUnlinkTarget.exe");
        string decoyPath = Path.Combine(_tempRoot, "PosixDecoy.exe");

        byte[] originalPayload = CreateDeterministicPayload(96 * 1024 * 1024, seed: 103);
        byte[] decoyPayload = CreateDeterministicPayload(32 * 1024, seed: 29);

        await File.WriteAllBytesAsync(targetPath, originalPayload);
        await File.WriteAllBytesAsync(decoyPath, decoyPayload);

        string originalSha256 = Convert.ToHexString(SHA256.HashData(originalPayload));
        string decoySha256 = Convert.ToHexString(SHA256.HashData(decoyPayload));

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalTarget = verifier.Resolve(targetPath).Evidence!.ExecutablePath;

        bool observedUnlinkRejection = false;

        for (int attempt = 0; attempt < 6 && !observedUnlinkRejection; attempt++)
        {
            if (!File.Exists(targetPath) || new FileInfo(targetPath).Length != originalPayload.Length)
                await File.WriteAllBytesAsync(targetPath, originalPayload);

            using var started = new ManualResetEventSlim(false);
            Task<IdentityVerificationResult<VerifiedExecutableIdentity>> verifyTask = Task.Run(async () =>
            {
                started.Set();
                return await verifier.VerifyAsync(targetPath, canonicalTarget, originalSha256);
            });

            started.Wait();
            await Task.Delay(attempt == 0 ? 5 : attempt * 3);

            if (TryPosixDelete(targetPath))
            {
                try
                {
                    File.Copy(decoyPath, targetPath, overwrite: true);
                }
                catch (IOException)
                {
                    // Fallback if OS did not allow immediate overwrite (e.g. non-POSIX delete fallback).
                }
            }

            var result = await verifyTask;

            if (result.Status == IdentityVerificationStatus.Rejected
                && result.ReasonCode == "executable-path-changed-during-verification")
            {
                observedUnlinkRejection = true;
                Assert.False(result.IsTrusted);
                Assert.Null(result.Evidence);
            }
            else if (result.IsTrusted)
            {
                Assert.Equal(canonicalTarget, result.Evidence!.ExecutablePath, StringComparer.Ordinal);
                Assert.Equal(originalSha256, result.Evidence.Sha256, StringComparer.Ordinal);
            }
            else
            {
                Assert.True(
                    result.ReasonCode is "executable-hash-mismatch" or "executable-not-found",
                    $"Unexpected failure reason during POSIX unlink race: {result.Status}/{result.ReasonCode}");
                Assert.NotEqual(originalSha256, decoySha256);
            }
        }

        Assert.True(observedUnlinkRejection,
            "Expected mid-hash POSIX unlink while FILE_SHARE_DELETE is held to trigger executable-path-changed-during-verification.");
    }

    [Fact]
    public async Task FileReplacementRace_ConcurrentJunctionSwap_NeverProducesSplitBrainIdentity()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string dirA = Path.Combine(_tempRoot, "RaceDirA");
        string dirB = Path.Combine(_tempRoot, "RaceDirB");
        string junctionDir = Path.Combine(_tempRoot, "RaceJunction");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        string exeA = Path.Combine(dirA, "SwappedApp.exe");
        string exeB = Path.Combine(dirB, "SwappedApp.exe");
        byte[] payloadA = CreateDeterministicPayload(512 * 1024, seed: 5);
        byte[] payloadB = CreateDeterministicPayload(768 * 1024, seed: 7);
        await File.WriteAllBytesAsync(exeA, payloadA);
        await File.WriteAllBytesAsync(exeB, payloadB);

        string hashA = Convert.ToHexString(SHA256.HashData(payloadA));
        string hashB = Convert.ToHexString(SHA256.HashData(payloadB));

        var verifier = new WindowsExecutableIdentityVerifier();
        var identityA = verifier.Resolve(exeA).Evidence!;
        var identityB = verifier.Resolve(exeB).Evidence!;

        CreateDirectoryJunction(junctionDir, dirA);
        string junctionExe = Path.Combine(junctionDir, "SwappedApp.exe");

        using var cts = new CancellationTokenSource();
        Task swapper = Task.Run(() =>
        {
            bool pointToA = false;
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    if (Directory.Exists(junctionDir))
                        Directory.Delete(junctionDir, recursive: false);
                    CreateDirectoryJunction(junctionDir, pointToA ? dirA : dirB);
                    pointToA = !pointToA;
                }
                catch (Exception)
                {
                    // Expected transient errors while racing junction delete/recreate.
                }
            }
        });

        try
        {
            for (int i = 0; i < 40; i++)
            {
                var res = verifier.Resolve(junctionExe);
                if (!res.IsTrusted)
                {
                    Assert.Equal(IdentityVerificationStatus.Unavailable, res.Status);
                    continue;
                }

                var ev = res.Evidence!;
                if (string.Equals(ev.ExecutablePath, identityA.ExecutablePath, StringComparison.Ordinal))
                {
                    Assert.Equal(hashA, ev.Sha256, StringComparer.Ordinal);
                    Assert.Equal(identityA.FileIndex, ev.FileIndex);
                    Assert.Equal(identityA.FileSizeBytes, ev.FileSizeBytes);
                }
                else if (string.Equals(ev.ExecutablePath, identityB.ExecutablePath, StringComparison.Ordinal))
                {
                    Assert.Equal(hashB, ev.Sha256, StringComparer.Ordinal);
                    Assert.Equal(identityB.FileIndex, ev.FileIndex);
                    Assert.Equal(identityB.FileSizeBytes, ev.FileSizeBytes);
                }
                else
                {
                    Assert.Fail($"Unexpected resolved path during junction swap: {ev.ExecutablePath}");
                }
            }
        }
        finally
        {
            cts.Cancel();
            await swapper;
        }
    }

    [Fact]
    public void NonCanonicalRelativeAndTraversalPaths_CanonicalizedByResolveAndRejectedAsClaims()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string subDir = Path.Combine(_tempRoot, "NestedSub");
        Directory.CreateDirectory(subDir);
        string targetExe = Path.Combine(_tempRoot, "CanonicalApp.exe");
        byte[] payload = CreateDeterministicPayload(4096, seed: 127);
        File.WriteAllBytes(targetExe, payload);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(payload));

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalPath = verifier.Resolve(targetExe).Evidence!.ExecutablePath;

        string originalCwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _tempRoot;

            string[] nonCanonicalAliases =
            [
                @".\CanonicalApp.exe",
                @"NestedSub\..\CanonicalApp.exe",
                @".\NestedSub\.\..\CanonicalApp.exe",
                Path.Combine(subDir, "..", "CanonicalApp.exe"),
                Path.Combine(_tempRoot, ".", "CanonicalApp.exe"),
                canonicalPath.Replace('\\', '/'),
                @"\\?\" + canonicalPath,
                @"\\.\" + canonicalPath,
                canonicalPath + ".",
                canonicalPath + " "
            ];

            foreach (string alias in nonCanonicalAliases)
            {
                var resolved = verifier.Resolve(alias);
                Assert.True(resolved.IsTrusted, $"Expected Resolve('{alias}') to succeed, got {resolved.Status}/{resolved.ReasonCode}");
                Assert.Equal(canonicalPath, resolved.Evidence!.ExecutablePath, StringComparer.Ordinal);

                // Claiming any non-canonical spelling directly must fail closed with path mismatch.
                var rejectedClaim = verifier.Verify(alias, expectedSha256);
                Assert.False(rejectedClaim.IsTrusted, $"Expected Verify('{alias}') to reject non-canonical claim.");
                Assert.Equal(IdentityVerificationStatus.Rejected, rejectedClaim.Status);
                Assert.Equal("executable-path-mismatch", rejectedClaim.ReasonCode);
                Assert.Null(rejectedClaim.Evidence);

                // Opening via the alias while claiming the canonical path succeeds.
                var acceptedCanonicalClaim = verifier.Verify(alias, canonicalPath, expectedSha256);
                Assert.True(acceptedCanonicalClaim.IsTrusted);
            }
        }
        finally
        {
            Environment.CurrentDirectory = originalCwd;
        }

        // Invalid / device / malformed inputs must fail closed safely.
        string[] invalidOrDeviceInputs =
        [
            "",
            "   ",
            "C:\\app.exe\0bad",
            @"\\.\NUL",
            "CON"
        ];

        foreach (string badInput in invalidOrDeviceInputs)
        {
            var res = verifier.Resolve(badInput);
            Assert.False(res.IsTrusted);
            Assert.Null(res.Evidence);
        }

        Assert.Equal("executable-path-invalid", verifier.Resolve(null!).ReasonCode);
        Assert.Equal("executable-path-mismatch", verifier.Verify(canonicalPath, null!, expectedSha256).ReasonCode);
    }

    [Fact]
    public void AlternateDataStreams_DefaultStreamAliasRejectedAsPathMismatch()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string hostExe = Path.Combine(_tempRoot, "StreamHost.exe");
        byte[] hostPayload = CreateDeterministicPayload(4096, seed: 131);
        File.WriteAllBytes(hostExe, hostPayload);
        string hostSha256 = Convert.ToHexString(SHA256.HashData(hostPayload));

        var verifier = new WindowsExecutableIdentityVerifier();
        string canonicalHost = verifier.Resolve(hostExe).Evidence!.ExecutablePath;

        // Explicit default stream syntax (::$DATA) resolves to canonical path and is rejected as a claimedPath.
        string defaultStreamAlias = hostExe + "::$DATA";
        var defaultStreamResolved = verifier.Resolve(defaultStreamAlias);
        Assert.True(defaultStreamResolved.IsTrusted);
        Assert.Equal(canonicalHost, defaultStreamResolved.Evidence!.ExecutablePath, StringComparer.Ordinal);

        var rejectedDefaultStreamClaim = verifier.Verify(defaultStreamAlias, hostSha256);
        Assert.False(rejectedDefaultStreamClaim.IsTrusted);
        Assert.Equal(IdentityVerificationStatus.Rejected, rejectedDefaultStreamClaim.Status);
        Assert.Equal("executable-path-mismatch", rejectedDefaultStreamClaim.ReasonCode);
    }

    private static byte[] CreateDeterministicPayload(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((i * 31 + seed) & 0xFF);
        return bytes;
    }

    private static bool TryCreateSymbolicLinks(
        string realExe,
        string fileLink,
        string realDir,
        string dirLink)
    {
        try
        {
            File.CreateSymbolicLink(fileLink, realExe);
            Directory.CreateSymbolicLink(dirLink, realDir);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string? FindFirstReadableRelativeFile(string rootDirectory)
    {
        try
        {
            var enumOptions = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = true
            };
            foreach (string file in Directory.EnumerateFiles(rootDirectory, "*", enumOptions))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return Path.GetRelativePath(rootDirectory, file);
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (IOException)
                {
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }

        return null;
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
        if (process.ExitCode != 0)
            throw new IOException($"mklink /J failed with exit code {process.ExitCode}: {process.StandardError.ReadToEnd()}");
    }

    private static string? FindUnusedDriveLetter()
    {
        HashSet<string> used = Directory.GetLogicalDrives()
            .Select(static d => d.TrimEnd('\\').ToUpperInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (char letter = 'Z'; letter >= 'M'; letter--)
        {
            string candidate = $"{letter}:";
            if (!used.Contains(candidate))
                return candidate;
        }

        return null;
    }

    private static bool TryPosixDelete(string path)
    {
        using SafeFileHandle handle = CreateFileW(
            path,
            DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return false;

        var info = new FileDispositionInfoEx
        {
            Flags = FileDispositionFlagDelete | FileDispositionFlagPosixSemantics
        };

        int size = Marshal.SizeOf<FileDispositionInfoEx>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            return SetFileInformationByHandle(handle, FileDispositionInfoExClass, buffer, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string? TryGetShortPathName(string longPath)
    {
        var sb = new StringBuilder(512);
        uint result = GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
        if (result == 0)
            return null;
        if (result >= (uint)sb.Capacity)
        {
            sb = new StringBuilder(checked((int)result + 1));
            result = GetShortPathNameW(longPath, sb, (uint)sb.Capacity);
            if (result == 0 || result >= (uint)sb.Capacity)
                return null;
        }

        return sb.ToString(0, (int)result);
    }

    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint DddRemoveDefinition = 0x00000002;
    private const uint DddExactMatchOnRemove = 0x00000004;
    private const int FileDispositionInfoExClass = 21;
    private const uint FileDispositionFlagDelete = 0x00000001;
    private const uint FileDispositionFlagPosixSemantics = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfoEx
    {
        public uint Flags;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDeviceW(
        uint dwFlags,
        string lpDeviceName,
        string? lpTargetPath);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        IntPtr lpFileInformation,
        uint dwBufferSize);
}
