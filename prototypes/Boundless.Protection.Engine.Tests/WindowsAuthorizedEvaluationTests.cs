using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class WindowsAuthorizedEvaluationTests : IDisposable
{
    private readonly string _tempRoot;

    public WindowsAuthorizedEvaluationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"bpe-win-auth-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempRoot))
            return;

        foreach (var dir in Directory.GetDirectories(_tempRoot, "*", SearchOption.TopDirectoryOnly))
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
    public void DefaultBoundaryVerifier_ExposesOperatingSystemVerifiedTrust()
    {
        var verifier = WindowsIdentityBoundaryVerifier.Default;

        Assert.NotNull(verifier);
        Assert.Equal(IdentityEvidenceSourceTrust.OperatingSystemVerified, verifier.SourceTrust);
        Assert.NotSame(verifier, WindowsIdentityBoundaryVerifier.Default);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_RealCurrentProcessAndExecutable_EvaluatesAllowDenyExceptionAndDefaultRestrict()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var target = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);

        var allowRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Allow,
            Priority: 10,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        // 1. Explicit Allow
        var allowResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule]);

        Assert.Equal(PolicyDecision.Allow, allowResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.AllowedByRule, allowResult.Status);
        Assert.False(allowResult.FailClosed);
        Assert.Equal(allowRule.RuleId, allowResult.MatchedRuleId);
        Assert.Equal("explicit-allow", allowResult.ReasonCode);
        Assert.True(allowResult.IsSimulation);
        Assert.False(allowResult.CanClaimProtection);

        // 2. Deny overrides Allow regardless of priority
        var denyRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Deny,
            Priority: -100,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var denyResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule, denyRule]);

        Assert.Equal(PolicyDecision.Restrict, denyResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByRule, denyResult.Status);
        Assert.True(denyResult.FailClosed);
        Assert.Equal(denyRule.RuleId, denyResult.MatchedRuleId);
        Assert.Equal("explicit-deny", denyResult.ReasonCode);
        Assert.True(denyResult.IsSimulation);
        Assert.False(denyResult.CanClaimProtection);

        // 3. Exception overrides Deny
        var exceptionRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Exception,
            Priority: -1000,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var exceptionResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule, denyRule, exceptionRule]);

        Assert.Equal(PolicyDecision.Allow, exceptionResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.AllowedByException, exceptionResult.Status);
        Assert.False(exceptionResult.FailClosed);
        Assert.Equal(exceptionRule.RuleId, exceptionResult.MatchedRuleId);
        Assert.Equal("exception-overrides-restriction", exceptionResult.ReasonCode);

        // 4. No matching rules defaults to Restrict (fail closed)
        var defaultResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            target,
            DateTimeOffset.UtcNow,
            []);

        Assert.Equal(PolicyDecision.Restrict, defaultResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByDefault, defaultResult.Status);
        Assert.True(defaultResult.FailClosed);
        Assert.Equal("no-matching-rule-fail-closed", defaultResult.ReasonCode);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_RealProcessWithNetworkTargetAndProtectedExclusion_EvaluatesCorrectly()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var networkTarget = PolicyTarget.Network("example.com", 443, NetworkProtocol.Tcp);
        var allowNetworkRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            networkTarget,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var allowedNet = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            networkTarget,
            DateTimeOffset.UtcNow,
            [allowNetworkRule]);

        Assert.Equal(PolicyDecision.Allow, allowedNet.Decision);
        Assert.Equal(PolicyEvaluationStatus.AllowedByRule, allowedNet.Status);
        Assert.False(allowedNet.FailClosed);
        Assert.False(allowedNet.CanClaimProtection);

        // Protected calling process executable is excluded before rule evaluation.
        var protectedCatalog = new ProtectedTargetCatalog([identity.CanonicalExecutablePath]);
        var protectedEvaluator = new ProtectionPolicyEvaluator(protectedCatalog);
        var appTarget = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);
        var allowAppRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            appTarget,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var excludedResult = await protectedEvaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            appTarget,
            DateTimeOffset.UtcNow,
            [allowAppRule]);

        Assert.Equal(PolicyDecision.ExcludedProtectedTarget, excludedResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.ExcludedProtectedTarget, excludedResult.Status);
        Assert.False(excludedResult.FailClosed);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_RealSpawnedChildProcess_AllowsWhileRunningAndFailsClosedAfterExit()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);

        var cmdPath = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var exeVerifier = new WindowsExecutableIdentityVerifier();
        var resolvedCmd = exeVerifier.Resolve(cmdPath);
        Assert.True(resolvedCmd.IsTrusted, resolvedCmd.ReasonCode);

        var sessionId = Guid.NewGuid();
        const long sessionGeneration = 2;
        var sessionClaims = new CallerSessionIdentityClaims(
            sessionId,
            sessionGeneration,
            osIdentity.UserSid,
            osIdentity.AuthenticationId);

        var target = PolicyTarget.Application(resolvedCmd.Evidence!.ExecutablePath, resolvedCmd.Evidence.Sha256);
        var allowRule = new EvaluationRule(
            Guid.NewGuid(),
            osIdentity.UserSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: sessionId,
            SessionGeneration: sessionGeneration);
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        var startInfo = new ProcessStartInfo
        {
            FileName = resolvedCmd.Evidence.ExecutablePath,
            Arguments = "/Q /K",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        CallerProcessIdentityClaims childClaims;
        using (var child = Process.Start(startInfo)!)
        {
            try
            {
                childClaims = new CallerProcessIdentityClaims(
                    child.Id,
                    child.StartTime.ToUniversalTime(),
                    osIdentity.UserSid,
                    resolvedCmd.Evidence.ExecutablePath,
                    resolvedCmd.Evidence.Sha256);

                var liveResult = await evaluator.EvaluateAuthorizedAsync(
                    sessionClaims,
                    childClaims,
                    target,
                    DateTimeOffset.UtcNow,
                    [allowRule]);

                Assert.Equal(PolicyDecision.Allow, liveResult.Decision);
                Assert.Equal(PolicyEvaluationStatus.AllowedByRule, liveResult.Status);
                Assert.False(liveResult.FailClosed);

                await child.StandardInput.WriteLineAsync("exit 259");
                await child.StandardInput.FlushAsync();
                child.StandardInput.Close();
                Assert.True(child.WaitForExit(5000));

                var exitedWhileHandleOpen = await evaluator.EvaluateAuthorizedAsync(
                    sessionClaims,
                    childClaims,
                    target,
                    DateTimeOffset.UtcNow,
                    [allowRule]);

                Assert.Equal(PolicyDecision.Restrict, exitedWhileHandleOpen.Decision);
                Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, exitedWhileHandleOpen.Status);
                Assert.Equal("process-already-exited", exitedWhileHandleOpen.ReasonCode);
                Assert.True(exitedWhileHandleOpen.FailClosed);
                Assert.False(exitedWhileHandleOpen.CanClaimProtection);
            }
            finally
            {
                if (!child.HasExited)
                    child.Kill(entireProcessTree: true);
            }
        }

        var reapedResult = await evaluator.EvaluateAuthorizedAsync(
            sessionClaims,
            childClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, reapedResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, reapedResult.Status);
        Assert.True(reapedResult.FailClosed);
        Assert.True(
            reapedResult.ReasonCode is "process-not-found" or "process-already-exited",
            $"Unexpected reason code after child exit/dispose: {reapedResult.ReasonCode}");
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_SpoofedSessionSidOrAuthContext_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var target = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);

        const string spoofedSid = "S-1-5-21-999999999-888888888-777777777-500";
        var effectiveSpoofedSid = string.Equals(identity.SubjectSid, spoofedSid, StringComparison.Ordinal)
            ? "S-1-5-21-999999999-888888888-777777777-501"
            : spoofedSid;

        // Spoofed session SID with matching allow rule for the spoofed SID still fails closed.
        var spoofedSession = identity.SessionClaims with { SubjectSid = effectiveSpoofedSid };
        var spoofedProcess = identity.ProcessClaims with { SubjectSid = effectiveSpoofedSid };
        var allowForSpoofed = new EvaluationRule(
            Guid.NewGuid(),
            effectiveSpoofedSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var sidResult = await evaluator.EvaluateAuthorizedAsync(
            spoofedSession,
            spoofedProcess,
            target,
            DateTimeOffset.UtcNow,
            [allowForSpoofed]);

        Assert.Equal(PolicyDecision.Restrict, sidResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, sidResult.Status);
        Assert.Equal("session-subject-sid-mismatch", sidResult.ReasonCode);
        Assert.True(sidResult.FailClosed);
        Assert.False(sidResult.CanClaimProtection);

        // Tampered AuthenticationContext LUID fails closed.
        var parsedLuid = ulong.Parse(identity.AuthenticationId[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var tamperedLuid = $"0x{parsedLuid ^ 1UL:x}";
        var tamperedAuthSession = identity.SessionClaims with { AuthenticationContext = tamperedLuid };
        var validAllowRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var luidResult = await evaluator.EvaluateAuthorizedAsync(
            tamperedAuthSession,
            identity.ProcessClaims,
            target,
            DateTimeOffset.UtcNow,
            [validAllowRule]);

        Assert.Equal(PolicyDecision.Restrict, luidResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, luidResult.Status);
        Assert.Equal("session-auth-context-mismatch", luidResult.ReasonCode);
        Assert.True(luidResult.FailClosed);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_SpoofedProcessSidOrStartTime_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var target = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);
        var allowRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        // Spoofed process owner SID
        var otherSid = string.Equals(identity.SubjectSid, "S-1-5-21-111111111-222222222-333333333-1001", StringComparison.Ordinal)
            ? "S-1-5-21-111111111-222222222-333333333-1002"
            : "S-1-5-21-111111111-222222222-333333333-1001";
        var spoofedProcessSid = identity.ProcessClaims with { SubjectSid = otherSid };

        var ownerMismatchResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            spoofedProcessSid,
            target,
            DateTimeOffset.UtcNow,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, ownerMismatchResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, ownerMismatchResult.Status);
        Assert.Equal("process-owner-sid-mismatch", ownerMismatchResult.ReasonCode);
        Assert.True(ownerMismatchResult.FailClosed);

        // Spoofed process start time (PID reuse)
        var wrongStartClaims = identity.ProcessClaims with
        {
            ProcessStartUtc = identity.ProcessClaims.ProcessStartUtc.AddSeconds(-10)
        };

        var pidReuseResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            wrongStartClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, pidReuseResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, pidReuseResult.Status);
        Assert.Equal("process-pid-reused-or-start-time-mismatch", pidReuseResult.ReasonCode);
        Assert.True(pidReuseResult.FailClosed);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_MismatchedExecutableHashOrPathOrAlias_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        // 1. Wrong SHA-256 hash
        var wrongHash = (identity.ExecutableSha256[0] == 'A' ? 'B' : 'A') + identity.ExecutableSha256[1..];
        var wrongHashProcess = identity.ProcessClaims with { ExecutableSha256 = wrongHash };
        var wrongHashTarget = PolicyTarget.Application(identity.CanonicalExecutablePath, wrongHash);
        var allowWrongHash = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            wrongHashTarget,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var hashMismatchResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            wrongHashProcess,
            wrongHashTarget,
            DateTimeOffset.UtcNow,
            [allowWrongHash]);

        Assert.Equal(PolicyDecision.Restrict, hashMismatchResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, hashMismatchResult.Status);
        Assert.Equal("executable-hash-mismatch", hashMismatchResult.ReasonCode);
        Assert.True(hashMismatchResult.FailClosed);

        // 2. Non-existent executable path
        var missingPath = Path.Combine(_tempRoot, "missing-executable.exe");
        var missingExeProcess = identity.ProcessClaims with { ExecutablePath = missingPath };
        var missingExeTarget = PolicyTarget.Application(missingPath, identity.ExecutableSha256);
        var allowMissing = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            missingExeTarget,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var missingExeResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            missingExeProcess,
            missingExeTarget,
            DateTimeOffset.UtcNow,
            [allowMissing]);

        Assert.Equal(PolicyDecision.Restrict, missingExeResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, missingExeResult.Status);
        Assert.Equal("executable-not-found", missingExeResult.ReasonCode);
        Assert.True(missingExeResult.FailClosed);

        // 3. Directory junction alias path fails closed (canonical path does not match alias claim)
        var realDir = Path.Combine(_tempRoot, "RealBin");
        var junctionDir = Path.Combine(_tempRoot, "JunctionBin");
        Directory.CreateDirectory(realDir);
        var realFile = Path.Combine(realDir, "TargetTool.exe");
        var payload = new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04 };
        await File.WriteAllBytesAsync(realFile, payload);
        var fileHash = Convert.ToHexString(SHA256.HashData(payload));
        CreateDirectoryJunction(junctionDir, realDir);

        var junctionFile = Path.Combine(junctionDir, "TargetTool.exe");
        var junctionProcess = identity.ProcessClaims with
        {
            ExecutablePath = junctionFile,
            ExecutableSha256 = fileHash
        };
        var junctionTarget = PolicyTarget.Application(junctionFile, fileHash);
        var allowJunction = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            junctionTarget,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var junctionResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            junctionProcess,
            junctionTarget,
            DateTimeOffset.UtcNow,
            [allowJunction]);

        Assert.Equal(PolicyDecision.Restrict, junctionResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, junctionResult.Status);
        Assert.Equal("executable-path-mismatch", junctionResult.ReasonCode);
        Assert.True(junctionResult.FailClosed);

        // 4. Directory path instead of file fails closed
        var directoryProcess = identity.ProcessClaims with { ExecutablePath = _tempRoot };
        var directoryTarget = PolicyTarget.Application(_tempRoot, identity.ExecutableSha256);

        var directoryResult = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            directoryProcess,
            directoryTarget,
            DateTimeOffset.UtcNow,
            [allowWrongHash]);

        Assert.Equal(PolicyDecision.Restrict, directoryResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, directoryResult.Status);
        Assert.Equal("executable-is-directory", directoryResult.ReasonCode);
        Assert.True(directoryResult.FailClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-999)]
    public async Task EvaluateAuthorizedAsync_InvalidProcessId_FailsClosed(int invalidPid)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var target = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);
        var allowRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var invalidClaims = identity.ProcessClaims with { ProcessId = invalidPid };
        var result = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            invalidClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, result.Status);
        Assert.Equal("invalid-process-id", result.ReasonCode);
        Assert.True(result.FailClosed);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_NonExistentProcessId_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var target = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);
        var allowRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        // Use a PID unlikely to exist (non-multiple-of-4 PID is invalid in Windows kernel handles, so OpenProcess fails with ERROR_INVALID_PARAMETER).
        const int nonExistentPid = 1_999_999_999;
        var missingProcessClaims = identity.ProcessClaims with { ProcessId = nonExistentPid };

        var result = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            missingProcessClaims,
            target,
            DateTimeOffset.UtcNow,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnavailable, result.Status);
        Assert.Equal("process-not-found", result.ReasonCode);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_HistoricalEvaluatedAtUtcBeforeVerification_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var identity = CaptureLiveCurrentProcessContext();
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var target = PolicyTarget.Application(identity.CanonicalExecutablePath, identity.ExecutableSha256);
        var allowRule = new EvaluationRule(
            Guid.NewGuid(),
            identity.SubjectSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: identity.SessionId,
            SessionGeneration: identity.SessionGeneration);

        var pastTime = DateTimeOffset.UtcNow.AddSeconds(-5);
        var result = await evaluator.EvaluateAuthorizedAsync(
            identity.SessionClaims,
            identity.ProcessClaims,
            target,
            pastTime,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, result.Status);
        Assert.Equal("verified-session-does-not-match-claims", result.ReasonCode);
        Assert.True(result.FailClosed);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_WhenNonWindowsPlatformSimulated_FailsClosedAsUnsupported()
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        var hash = new string('a', 64);
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", hash);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var sessionClaims = new CallerSessionIdentityClaims(sessionId, 1, sid, "0x1");
        var processClaims = new CallerProcessIdentityClaims(4242, now.AddSeconds(-5), sid, "C:\\BPE-Synthetic\\reader.exe", hash);
        var allowRule = new EvaluationRule(Guid.NewGuid(), sid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 1);

        var evaluator = new ProtectionPolicyEvaluator(
            new ProtectedTargetCatalog(),
            maximumIdentityAge: null,
            identityVerifier: WindowsIdentityBoundaryVerifier.Default,
            isWindows: () => false);

        var result = await evaluator.EvaluateAuthorizedAsync(sessionClaims, processClaims, target, now, [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnsupported, result.Status);
        Assert.Equal("platform-unsupported-windows-identity-required", result.ReasonCode);
        Assert.True(result.FailClosed);
        Assert.True(result.IsSimulation);
        Assert.False(result.CanClaimProtection);

        var boundaryVerifier = new WindowsIdentityBoundaryVerifier(
            new WindowsSessionIdentityVerifier(),
            new WindowsProcessIdentityVerifier(),
            new WindowsExecutableIdentityVerifier(),
            isWindows: () => false);

        var sessionResult = await boundaryVerifier.VerifySessionAsync(sessionClaims);
        Assert.Equal(IdentityVerificationStatus.Unsupported, sessionResult.Status);
        Assert.Equal("platform-unsupported-windows-identity-required", sessionResult.ReasonCode);
        Assert.False(sessionResult.IsTrusted);
        Assert.Null(sessionResult.Evidence);

        var fakeSessionEvidence = new VerifiedSessionIdentity(sessionId, 1, sid, "0x1", now, now, now.AddHours(1));
        var processResult = await boundaryVerifier.VerifyProcessAsync(processClaims, fakeSessionEvidence);
        Assert.Equal(IdentityVerificationStatus.Unsupported, processResult.Status);
        Assert.Equal("platform-unsupported-windows-identity-required", processResult.ReasonCode);
        Assert.False(processResult.IsTrusted);
        Assert.Null(processResult.Evidence);
    }

    [Theory]
    [InlineData(IdentityEvidenceSourceTrust.SimulatedUntrusted)]
    [InlineData(IdentityEvidenceSourceTrust.CallerSupplied)]
    public async Task EvaluateAuthorizedAsync_WithSimulatedOrCallerSuppliedVerifier_FailsClosedWithoutCallingVerifier(
        IdentityEvidenceSourceTrust untrustedSource)
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        var hash = new string('a', 64);
        var target = PolicyTarget.Application("C:\\BPE-Synthetic\\reader.exe", hash);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        var verifier = new UntrustedStubVerifier(untrustedSource);
        var evaluator = new ProtectionPolicyEvaluator(
            new ProtectedTargetCatalog(),
            maximumIdentityAge: null,
            identityVerifier: verifier,
            isWindows: () => true);

        var allowRule = new EvaluationRule(Guid.NewGuid(), sid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 1);

        var result = await evaluator.EvaluateAuthorizedAsync(
            new CallerSessionIdentityClaims(sessionId, 1, sid, "0x1"),
            new CallerProcessIdentityClaims(4242, now.AddSeconds(-5), sid, "C:\\BPE-Synthetic\\reader.exe", hash),
            target,
            now,
            [allowRule]);

        Assert.False(verifier.WasInvoked);
        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnsupported, result.Status);
        Assert.Equal("verifier-source-not-trusted", result.ReasonCode);
        Assert.True(result.FailClosed);
        Assert.True(result.IsSimulation);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_AndBoundaryVerifier_PropagateCancellation()
    {
        const string sid = "S-1-5-21-111111111-222222222-333333333-1001";
        var hash = new string('a', 64);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var sessionClaims = new CallerSessionIdentityClaims(sessionId, 1, sid, "0x1");
        var processClaims = new CallerProcessIdentityClaims(4242, now.AddSeconds(-5), sid, "C:\\BPE-Synthetic\\reader.exe", hash);
        var verifiedSession = new VerifiedSessionIdentity(sessionId, 1, sid, "0x1", now, now, now.AddHours(1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var boundary = WindowsIdentityBoundaryVerifier.Default;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.VerifySessionAsync(sessionClaims, cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => boundary.VerifyProcessAsync(processClaims, verifiedSession, cts.Token));
    }

    private static LiveCurrentProcessContext CaptureLiveCurrentProcessContext()
    {
        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        using var current = Process.GetCurrentProcess();
        var rawProcessPath = Environment.ProcessPath;
        Assert.False(string.IsNullOrWhiteSpace(rawProcessPath));

        var exeResult = new WindowsExecutableIdentityVerifier().Resolve(rawProcessPath!);
        Assert.True(exeResult.IsTrusted, exeResult.ReasonCode);
        Assert.NotNull(exeResult.Evidence);

        var sessionId = Guid.NewGuid();
        const long sessionGeneration = 1;
        var sessionClaims = new CallerSessionIdentityClaims(
            sessionId,
            sessionGeneration,
            osIdentity.UserSid,
            osIdentity.AuthenticationId);
        var processClaims = new CallerProcessIdentityClaims(
            current.Id,
            current.StartTime.ToUniversalTime(),
            osIdentity.UserSid,
            exeResult.Evidence!.ExecutablePath,
            exeResult.Evidence.Sha256);

        return new LiveCurrentProcessContext(
            sessionId,
            sessionGeneration,
            osIdentity.UserSid,
            osIdentity.AuthenticationId,
            exeResult.Evidence.ExecutablePath,
            exeResult.Evidence.Sha256,
            sessionClaims,
            processClaims);
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
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    private sealed record LiveCurrentProcessContext(
        Guid SessionId,
        long SessionGeneration,
        string SubjectSid,
        string AuthenticationId,
        string CanonicalExecutablePath,
        string ExecutableSha256,
        CallerSessionIdentityClaims SessionClaims,
        CallerProcessIdentityClaims ProcessClaims);

    private sealed class UntrustedStubVerifier(IdentityEvidenceSourceTrust sourceTrust) : IIdentityBoundaryVerifier
    {
        public bool WasInvoked { get; private set; }
        public IdentityEvidenceSourceTrust SourceTrust { get; } = sourceTrust;

        public Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
            CallerSessionIdentityClaims claims,
            CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            return Task.FromResult(IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("simulated"));
        }

        public Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
            CallerProcessIdentityClaims claims,
            VerifiedSessionIdentity session,
            CancellationToken cancellationToken = default)
        {
            WasInvoked = true;
            return Task.FromResult(IdentityVerificationResult<VerifiedProcessIdentity>.Simulated("simulated"));
        }
    }
}
