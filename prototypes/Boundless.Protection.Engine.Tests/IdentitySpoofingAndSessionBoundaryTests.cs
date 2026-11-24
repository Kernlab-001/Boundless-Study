using System.Globalization;
using System.Reflection;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class IdentitySpoofingAndSessionBoundaryTests
{
    private const string InteractiveSid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string InteractiveLuid = "0x1234abcd";
    private const string SyntheticExecutablePath = "C:\\BPE-Synthetic\\reader.exe";
    private const string SyntheticExecutableHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset BaseTimeUtc = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    [InlineData("S-1-1-0")]
    [InlineData("S-1-5-11")]
    [InlineData("S-1-5-32-545")]
    [InlineData("S-1-5-21-999999999-888888888-777777777-500")]
    [InlineData("s-1-5-21-111111111-222222222-333333333-1001")]
    [InlineData(" S-1-5-21-111111111-222222222-333333333-1001 ")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task VerifySessionAsync_SpoofedOrWellKnownSid_FailsClosed(string spoofedSid)
    {
        var verifier = CreateSessionVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, spoofedSid, InteractiveLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("session-subject-sid-mismatch", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task VerifySessionAsync_NullSubjectSid_FailsClosed()
    {
        var verifier = CreateSessionVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, null!, InteractiveLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("session-subject-sid-mismatch", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-999999999-888888888-777777777-500")]
    public async Task VerifySessionAsync_AgainstRealOsToken_SpoofedWellKnownSid_FailsClosed(string spoofedSid)
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        if (string.Equals(osIdentity.UserSid, spoofedSid, StringComparison.Ordinal))
            return;

        var verifier = new WindowsSessionIdentityVerifier();
        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, spoofedSid, osIdentity.AuthenticationId));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("session-subject-sid-mismatch", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData("0x0")]
    [InlineData("0x3e7")]
    [InlineData("0x3e4")]
    [InlineData("0x3e5")]
    [InlineData("0x3e6")]
    [InlineData("0x1234abce")]
    [InlineData("0X1234ABCD")]
    [InlineData(" 0x1234abcd ")]
    [InlineData("1234abcd")]
    [InlineData("forged-luid")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task VerifySessionAsync_ForgedAuthenticationContext_FailsClosed(string forgedAuthContext)
    {
        var verifier = CreateSessionVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, InteractiveSid, forgedAuthContext));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("session-auth-context-mismatch", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task VerifySessionAsync_AgainstRealOsToken_TamperedLogonLuid_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var parsed = ulong.Parse(osIdentity.AuthenticationId[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var tamperedLuid = $"0x{parsed ^ 1UL:x}";

        var verifier = new WindowsSessionIdentityVerifier();
        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, osIdentity.UserSid, tamperedLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("session-auth-context-mismatch", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task VerifySessionAsync_ZeroGuidSessionId_FailsClosed()
    {
        var verifier = CreateSessionVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.Empty, 1, InteractiveSid, InteractiveLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("invalid-session-id", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Theory]
    [InlineData(1u, 0u)]
    [InlineData(1u, 2u)]
    [InlineData(0u, 1u)]
    [InlineData(2u, 1u)]
    public async Task VerifyProcessAsync_CrossSessionProcessMismatch_FailsClosed(uint tokenSessionId, uint pidSessionId)
    {
        var probe = new StubWindowsProcessProbe
        {
            StartUtc = BaseTimeUtc.AddSeconds(-10),
            UserSid = InteractiveSid,
            TokenSessionId = tokenSessionId,
            PidSessionId = pidSessionId
        };
        var processVerifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseTimeUtc);
        var sessionVerifier = CreateSessionVerifier(windowsSessionId: 1);
        var sessionResult = await sessionVerifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, InteractiveSid, InteractiveLuid));
        Assert.True(sessionResult.IsTrusted);

        var processResult = await processVerifier.VerifyProcessAsync(
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-10), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            sessionResult.Evidence!);

        Assert.Equal(IdentityVerificationStatus.Rejected, processResult.Status);
        Assert.Equal("process-pid-reused-or-session-mismatch", processResult.ReasonCode);
        Assert.False(processResult.IsTrusted);
        Assert.Null(processResult.Evidence);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_CrossSessionBoundaryClaimsAndRules_FailClosed()
    {
        var sessionA = Guid.NewGuid();
        var sessionB = Guid.NewGuid();
        var target = PolicyTarget.Application(SyntheticExecutablePath, SyntheticExecutableHash);
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        var sessionVerifier = CreateSessionVerifier(windowsSessionId: 1);
        var probe = new StubWindowsProcessProbe
        {
            StartUtc = BaseTimeUtc.AddSeconds(-5),
            UserSid = InteractiveSid,
            TokenSessionId = 1,
            PidSessionId = 1
        };
        var processVerifier = new WindowsProcessIdentityVerifier(probe, () => true, () => BaseTimeUtc);
        var boundaryVerifier = new CompositeOsBoundaryVerifier(sessionVerifier, processVerifier);

        // Rule granted for Session A must not authorize Session B.
        var allowRuleSessionA = new EvaluationRule(
            Guid.NewGuid(),
            InteractiveSid,
            target,
            PolicyRuleEffect.Allow,
            SessionId: sessionA,
            SessionGeneration: 1);

        var crossSessionResult = await evaluator.EvaluateAuthorizedAsync(
            boundaryVerifier,
            new CallerSessionIdentityClaims(sessionB, 1, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            BaseTimeUtc,
            [allowRuleSessionA]);

        Assert.Equal(PolicyDecision.Restrict, crossSessionResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByDefault, crossSessionResult.Status);
        Assert.True(crossSessionResult.FailClosed);
        Assert.False(crossSessionResult.CanClaimProtection);

        // Mismatched SessionId returned by verifier vs caller claims fails closed.
        var tamperedBoundary = new ScriptedBoundaryVerifier(
            IdentityEvidenceSourceTrust.OperatingSystemVerified,
            _ => IdentityVerificationResult<VerifiedSessionIdentity>.Verified(
                new VerifiedSessionIdentity(sessionA, 1, InteractiveSid, InteractiveLuid, BaseTimeUtc, BaseTimeUtc, BaseTimeUtc.AddHours(1))),
            (_, s) => IdentityVerificationResult<VerifiedProcessIdentity>.Verified(
                new VerifiedProcessIdentity(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash, s.SessionId, s.Generation, BaseTimeUtc)));

        var mismatchedSessionClaimResult = await evaluator.EvaluateAuthorizedAsync(
            tamperedBoundary,
            new CallerSessionIdentityClaims(sessionB, 1, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            BaseTimeUtc,
            [allowRuleSessionA]);

        Assert.Equal(PolicyDecision.Restrict, mismatchedSessionClaimResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, mismatchedSessionClaimResult.Status);
        Assert.Equal("verified-session-does-not-match-claims", mismatchedSessionClaimResult.ReasonCode);
        Assert.True(mismatchedSessionClaimResult.FailClosed);
        Assert.False(mismatchedSessionClaimResult.CanClaimProtection);

        // Process bound to different session ID than verified session fails IsTrustedProcess.
        var sessionEvidence = new VerifiedSessionIdentity(sessionA, 1, InteractiveSid, InteractiveLuid, BaseTimeUtc, BaseTimeUtc, BaseTimeUtc.AddHours(1));
        var foreignProcessResult = IdentityVerificationResult<VerifiedProcessIdentity>.Verified(
            new VerifiedProcessIdentity(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash, sessionB, 1, BaseTimeUtc));
        Assert.False(IdentityTrustBoundary.IsTrustedProcess(boundaryVerifier, foreignProcessResult, sessionEvidence));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-60)]
    public async Task EvaluateAuthorizedAsync_EvaluationBeforeStartsAtUtc_FailsClosed(int secondsOffsetFromStart)
    {
        var sessionId = Guid.NewGuid();
        var startsAt = BaseTimeUtc;
        var authenticatedAt = BaseTimeUtc.AddSeconds(-120);
        var expiresAt = BaseTimeUtc.AddHours(1);
        var evaluatedAt = startsAt.AddSeconds(secondsOffsetFromStart);

        var result = await EvaluateWithSessionTimestamps(
            sessionId,
            authenticatedAt,
            startsAt,
            expiresAt,
            evaluatedAt);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, result.Status);
        Assert.Equal("verified-session-does-not-match-claims", result.ReasonCode);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3600)]
    public async Task EvaluateAuthorizedAsync_EvaluationAtOrAfterExpiresAtUtc_FailsClosed(int secondsPastExpiry)
    {
        var sessionId = Guid.NewGuid();
        var authenticatedAt = BaseTimeUtc.AddHours(-1);
        var startsAt = authenticatedAt;
        var expiresAt = BaseTimeUtc;
        var evaluatedAt = expiresAt.AddSeconds(secondsPastExpiry);

        var result = await EvaluateWithSessionTimestamps(
            sessionId,
            authenticatedAt,
            startsAt,
            expiresAt,
            evaluatedAt);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, result.Status);
        Assert.Equal("verified-session-does-not-match-claims", result.ReasonCode);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_EvaluationBeforeAuthenticatedAtUtc_FailsClosed()
    {
        var sessionId = Guid.NewGuid();
        var startsAt = BaseTimeUtc.AddMinutes(-10);
        var authenticatedAt = BaseTimeUtc;
        var expiresAt = BaseTimeUtc.AddHours(1);
        var evaluatedAt = authenticatedAt.AddSeconds(-1);

        var result = await EvaluateWithSessionTimestamps(
            sessionId,
            authenticatedAt,
            startsAt,
            expiresAt,
            evaluatedAt);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, result.Status);
        Assert.Equal("verified-session-does-not-match-claims", result.ReasonCode);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_IssuedSessionExpiresAfterConfiguredLifetime_FailsClosed()
    {
        var sessionId = Guid.NewGuid();
        var verifier = CreateSessionVerifier(now: () => BaseTimeUtc, sessionLifetime: TimeSpan.FromMinutes(30));
        var issued = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(sessionId, 1, InteractiveSid, InteractiveLuid));
        Assert.True(issued.IsTrusted);

        var expiredCheckTime = BaseTimeUtc.AddMinutes(30);
        var target = PolicyTarget.Application(SyntheticExecutablePath, SyntheticExecutableHash);
        var allowRule = new EvaluationRule(Guid.NewGuid(), InteractiveSid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 1);
        var boundary = new ScriptedBoundaryVerifier(
            IdentityEvidenceSourceTrust.OperatingSystemVerified,
            _ => issued,
            (_, s) => IdentityVerificationResult<VerifiedProcessIdentity>.Verified(
                new VerifiedProcessIdentity(4242, expiredCheckTime.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash, s.SessionId, s.Generation, expiredCheckTime)));

        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        var result = await evaluator.EvaluateAuthorizedAsync(
            boundary,
            new CallerSessionIdentityClaims(sessionId, 1, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, expiredCheckTime.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            expiredCheckTime,
            [allowRule]);

        Assert.Equal(PolicyDecision.Restrict, result.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, result.Status);
        Assert.True(result.FailClosed);
        Assert.False(result.CanClaimProtection);
    }

    [Fact]
    public async Task EvaluateAuthorizedAsync_NonUtcOrInvertedSessionTimestamps_FailClosed()
    {
        var sessionId = Guid.NewGuid();
        var nonUtc = new DateTimeOffset(2026, 11, 24, 12, 0, 0, TimeSpan.FromHours(2));

        var nonUtcResult = await EvaluateWithSessionTimestamps(
            sessionId,
            nonUtc,
            nonUtc,
            nonUtc.AddHours(1),
            nonUtc.ToUniversalTime().AddMinutes(5));
        Assert.Equal(PolicyDecision.Restrict, nonUtcResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, nonUtcResult.Status);

        var invertedResult = await EvaluateWithSessionTimestamps(
            sessionId,
            BaseTimeUtc,
            BaseTimeUtc.AddMinutes(10),
            BaseTimeUtc.AddMinutes(10),
            BaseTimeUtc.AddMinutes(10));
        Assert.Equal(PolicyDecision.Restrict, invertedResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, invertedResult.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-99)]
    [InlineData(long.MinValue)]
    public async Task VerifySessionAsync_ZeroOrNegativeGeneration_FailsClosed(long tamperedGeneration)
    {
        var verifier = CreateSessionVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), tamperedGeneration, InteractiveSid, InteractiveLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.Equal("invalid-session-generation", result.ReasonCode);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task GenerationTampering_AcrossProcessRuleAndEngineBoundaries_FailsClosed()
    {
        var sessionId = Guid.NewGuid();
        var target = PolicyTarget.Application(SyntheticExecutablePath, SyntheticExecutableHash);
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        // 1. Mismatched generation between verified session (gen 2) and caller claim (gen 3).
        var sessionGen2 = new VerifiedSessionIdentity(sessionId, 2, InteractiveSid, InteractiveLuid, BaseTimeUtc, BaseTimeUtc, BaseTimeUtc.AddHours(1));
        var mismatchBoundary = new ScriptedBoundaryVerifier(
            IdentityEvidenceSourceTrust.OperatingSystemVerified,
            _ => IdentityVerificationResult<VerifiedSessionIdentity>.Verified(sessionGen2),
            (_, s) => IdentityVerificationResult<VerifiedProcessIdentity>.Verified(
                new VerifiedProcessIdentity(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash, s.SessionId, s.Generation, BaseTimeUtc)));
        var allowRuleGen3 = new EvaluationRule(Guid.NewGuid(), InteractiveSid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 3);

        var mismatchClaimResult = await evaluator.EvaluateAuthorizedAsync(
            mismatchBoundary,
            new CallerSessionIdentityClaims(sessionId, 3, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            BaseTimeUtc,
            [allowRuleGen3]);

        Assert.Equal(PolicyDecision.Restrict, mismatchClaimResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityRejected, mismatchClaimResult.Status);
        Assert.True(mismatchClaimResult.FailClosed);

        // 2. Process evidence bound to stale generation 1 cannot pass IsTrustedProcess for session generation 2.
        var staleProcessResult = IdentityVerificationResult<VerifiedProcessIdentity>.Verified(
            new VerifiedProcessIdentity(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash, sessionId, 1, BaseTimeUtc));
        Assert.False(IdentityTrustBoundary.IsTrustedProcess(mismatchBoundary, staleProcessResult, sessionGen2));

        // 3. Rule bound to generation 1 cannot authorize verified session generation 2.
        var allowRuleGen1 = new EvaluationRule(Guid.NewGuid(), InteractiveSid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 1);
        var staleRuleResult = await evaluator.EvaluateAuthorizedAsync(
            mismatchBoundary,
            new CallerSessionIdentityClaims(sessionId, 2, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            BaseTimeUtc,
            [allowRuleGen1]);

        Assert.Equal(PolicyDecision.Restrict, staleRuleResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.RestrictedByDefault, staleRuleResult.Status);
        Assert.True(staleRuleResult.FailClosed);

        // 4. WindowsProcessIdentityVerifier rejects session evidence with generation <= 0.
        var processVerifier = new WindowsProcessIdentityVerifier(
            new StubWindowsProcessProbe { StartUtc = BaseTimeUtc.AddSeconds(-5), UserSid = InteractiveSid },
            () => true,
            () => BaseTimeUtc);
        var badSessionProcessResult = await processVerifier.VerifyProcessAsync(
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            new VerifiedSessionIdentity(sessionId, 0, InteractiveSid, InteractiveLuid, BaseTimeUtc, BaseTimeUtc, BaseTimeUtc.AddHours(1)));
        Assert.Equal(IdentityVerificationStatus.Rejected, badSessionProcessResult.Status);
        Assert.Equal("invalid-session-identity", badSessionProcessResult.ReasonCode);
    }

    [Fact]
    public void Reflection_UntrustedCallerClaimsCanNeverBePromotedToVerifiedEvidenceWithoutOsVerification()
    {
        var assembly = typeof(WindowsSessionIdentityVerifier).Assembly;

        Type[] evidenceTypes =
        [
            typeof(VerifiedSessionIdentity),
            typeof(VerifiedProcessIdentity),
            typeof(VerifiedExecutableIdentity)
        ];

        foreach (var evidenceType in evidenceTypes)
        {
            Assert.Empty(evidenceType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

            var mutablePublicProperties = evidenceType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true })
                .ToArray();
            Assert.Empty(mutablePublicProperties);

            var conversionOperators = evidenceType
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.IsSpecialName && m.Name is "op_Implicit" or "op_Explicit")
                .ToArray();
            Assert.Empty(conversionOperators);
        }

        // IdentityVerificationResult<T> exposes no public constructor or public Verified/trusted factory.
        var resultOpenType = typeof(IdentityVerificationResult<>);
        Assert.Empty(resultOpenType.GetConstructors(BindingFlags.Public | BindingFlags.Instance));

        var publicResultMethods = resultOpenType
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName)
            .Select(m => m.Name)
            .ToArray();
        Assert.Equal([nameof(IdentityVerificationResult<VerifiedSessionIdentity>.Simulated)], publicResultMethods);

        // OS-backed verifiers expose only OS-backed parameterless constructors publicly.
        foreach (var osVerifierType in new[]
        {
            typeof(WindowsSessionIdentityVerifier),
            typeof(WindowsProcessIdentityVerifier),
            typeof(WindowsExecutableIdentityVerifier),
            typeof(WindowsIdentityBoundaryVerifier)
        })
        {
            var publicCtors = osVerifierType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            var singleCtor = Assert.Single(publicCtors);
            Assert.Empty(singleCtor.GetParameters());
        }

        // No public method outside the OS-backed verifiers promotes caller claims to Verified* or IdentityVerificationResult<Verified*>.
        var osVerifierTypes = new HashSet<Type>
        {
            typeof(WindowsSessionIdentityVerifier),
            typeof(WindowsProcessIdentityVerifier),
            typeof(WindowsExecutableIdentityVerifier),
            typeof(WindowsIdentityBoundaryVerifier)
        };
        var claimTypes = new HashSet<Type>
        {
            typeof(CallerSessionIdentityClaims),
            typeof(CallerProcessIdentityClaims),
            typeof(ProtectionSessionContext),
            typeof(ProcessIdentitySnapshot)
        };

        var suspiciousPublicPromoters = assembly.GetTypes()
            .Where(t => t.IsPublic && !osVerifierTypes.Contains(t))
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(m =>
            {
                var returnsVerified = evidenceTypes.Contains(m.ReturnType)
                    || (m.ReturnType.IsGenericType
                        && (m.ReturnType.GetGenericTypeDefinition() == typeof(IdentityVerificationResult<>)
                            || m.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
                        && m.ReturnType.GetGenericArguments().Any(arg =>
                            evidenceTypes.Contains(arg)
                            || (arg.IsGenericType && arg.GetGenericTypeDefinition() == typeof(IdentityVerificationResult<>))));
                return returnsVerified && m.GetParameters().Any(p => claimTypes.Contains(p.ParameterType));
            })
            .ToArray();
        Assert.Empty(suspiciousPublicPromoters);

        // IIdentityBoundaryVerifier and IdentityTrustBoundary are strictly internal.
        var boundaryInterface = assembly.GetType("Boundless.Protection.Engine.IIdentityBoundaryVerifier", throwOnError: true)!;
        var trustBoundary = assembly.GetType("Boundless.Protection.Engine.IdentityTrustBoundary", throwOnError: true)!;
        Assert.False(boundaryInterface.IsPublic);
        Assert.False(trustBoundary.IsPublic);
    }

    [Theory]
    [InlineData(IdentityEvidenceSourceTrust.SimulatedUntrusted)]
    [InlineData(IdentityEvidenceSourceTrust.CallerSupplied)]
    public async Task SimulatedAndCallerSuppliedSources_CannotMintTrustedEvidenceOrAuthorize(IdentityEvidenceSourceTrust untrustedSource)
    {
        var simulatedSession = IdentityVerificationResult<VerifiedSessionIdentity>.Simulated("simulated-session");
        var simulatedProcess = IdentityVerificationResult<VerifiedProcessIdentity>.Simulated("simulated-process");

        Assert.Equal(IdentityVerificationStatus.Rejected, simulatedSession.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.SimulatedUntrusted, simulatedSession.SourceTrust);
        Assert.Null(simulatedSession.Evidence);
        Assert.False(simulatedSession.IsTrusted);

        Assert.Equal(IdentityVerificationStatus.Rejected, simulatedProcess.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.SimulatedUntrusted, simulatedProcess.SourceTrust);
        Assert.Null(simulatedProcess.Evidence);
        Assert.False(simulatedProcess.IsTrusted);

        // Even if an internal untrusted verifier tries to wrap evidence in Verified(), trust gates reject it.
        var sessionId = Guid.NewGuid();
        var forgedSessionEvidence = new VerifiedSessionIdentity(
            sessionId, 1, InteractiveSid, InteractiveLuid, BaseTimeUtc, BaseTimeUtc, BaseTimeUtc.AddHours(1));
        var forgedProcessEvidence = new VerifiedProcessIdentity(
            4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash, sessionId, 1, BaseTimeUtc);

        bool invoked = false;
        var untrustedVerifier = new ScriptedBoundaryVerifier(
            untrustedSource,
            _ =>
            {
                invoked = true;
                return IdentityVerificationResult<VerifiedSessionIdentity>.Verified(forgedSessionEvidence);
            },
            (_, _) =>
            {
                invoked = true;
                return IdentityVerificationResult<VerifiedProcessIdentity>.Verified(forgedProcessEvidence);
            });

        var osVerifier = new CompositeOsBoundaryVerifier(
            CreateSessionVerifier(),
            new WindowsProcessIdentityVerifier(new StubWindowsProcessProbe(), () => true, () => BaseTimeUtc));

        Assert.False(IdentityTrustBoundary.IsTrustedSession(untrustedVerifier, IdentityVerificationResult<VerifiedSessionIdentity>.Verified(forgedSessionEvidence)));
        Assert.False(IdentityTrustBoundary.IsTrustedSession(osVerifier, simulatedSession));
        Assert.False(IdentityTrustBoundary.IsTrustedProcess(untrustedVerifier, IdentityVerificationResult<VerifiedProcessIdentity>.Verified(forgedProcessEvidence), forgedSessionEvidence));
        Assert.False(IdentityTrustBoundary.IsTrustedProcess(osVerifier, simulatedProcess, forgedSessionEvidence));

        var target = PolicyTarget.Application(SyntheticExecutablePath, SyntheticExecutableHash);
        var allowRule = new EvaluationRule(Guid.NewGuid(), InteractiveSid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 1);
        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());

        var evalResult = await evaluator.EvaluateAuthorizedAsync(
            untrustedVerifier,
            new CallerSessionIdentityClaims(sessionId, 1, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, BaseTimeUtc.AddSeconds(-5), InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            BaseTimeUtc,
            [allowRule]);

        Assert.False(invoked);
        Assert.Equal(PolicyDecision.Restrict, evalResult.Decision);
        Assert.Equal(PolicyEvaluationStatus.IdentityUnsupported, evalResult.Status);
        Assert.Equal("verifier-source-not-trusted", evalResult.ReasonCode);
        Assert.True(evalResult.FailClosed);
        Assert.False(evalResult.CanClaimProtection);
    }

    private static WindowsSessionIdentityVerifier CreateSessionVerifier(
        uint windowsSessionId = 1,
        Func<DateTimeOffset>? now = null,
        TimeSpan? sessionLifetime = null) =>
        new(
            () => true,
            (out WindowsTokenSessionIdentity identity, out string error) =>
            {
                identity = new WindowsTokenSessionIdentity(InteractiveSid, InteractiveLuid, windowsSessionId);
                error = string.Empty;
                return true;
            },
            now ?? (() => BaseTimeUtc),
            sessionLifetime);

    private static async Task<PolicyEvaluationResult> EvaluateWithSessionTimestamps(
        Guid sessionId,
        DateTimeOffset authenticatedAtUtc,
        DateTimeOffset startsAtUtc,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset evaluatedAtUtc)
    {
        var target = PolicyTarget.Application(SyntheticExecutablePath, SyntheticExecutableHash);
        var allowRule = new EvaluationRule(Guid.NewGuid(), InteractiveSid, target, PolicyRuleEffect.Allow,
            SessionId: sessionId, SessionGeneration: 1);

        var sessionEvidence = new VerifiedSessionIdentity(
            sessionId,
            1,
            InteractiveSid,
            InteractiveLuid,
            authenticatedAtUtc,
            startsAtUtc,
            expiresAtUtc);

        var processStartUtc = evaluatedAtUtc.ToUniversalTime().AddSeconds(-5);
        var observedAtUtc = evaluatedAtUtc.ToUniversalTime();
        var processEvidence = new VerifiedProcessIdentity(
            4242,
            processStartUtc,
            InteractiveSid,
            SyntheticExecutablePath,
            SyntheticExecutableHash,
            sessionId,
            1,
            observedAtUtc);

        var verifier = new ScriptedBoundaryVerifier(
            IdentityEvidenceSourceTrust.OperatingSystemVerified,
            _ => IdentityVerificationResult<VerifiedSessionIdentity>.Verified(sessionEvidence),
            (_, _) => IdentityVerificationResult<VerifiedProcessIdentity>.Verified(processEvidence));

        var evaluator = new ProtectionPolicyEvaluator(new ProtectedTargetCatalog());
        return await evaluator.EvaluateAuthorizedAsync(
            verifier,
            new CallerSessionIdentityClaims(sessionId, 1, InteractiveSid, InteractiveLuid),
            new CallerProcessIdentityClaims(4242, processStartUtc, InteractiveSid, SyntheticExecutablePath, SyntheticExecutableHash),
            target,
            evaluatedAtUtc,
            [allowRule]);
    }

    private sealed class CompositeOsBoundaryVerifier(
        WindowsSessionIdentityVerifier sessionVerifier,
        WindowsProcessIdentityVerifier processVerifier) : IIdentityBoundaryVerifier
    {
        public IdentityEvidenceSourceTrust SourceTrust => IdentityEvidenceSourceTrust.OperatingSystemVerified;

        public Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
            CallerSessionIdentityClaims claims,
            CancellationToken cancellationToken = default) =>
            sessionVerifier.VerifySessionAsync(claims, cancellationToken);

        public Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
            CallerProcessIdentityClaims claims,
            VerifiedSessionIdentity session,
            CancellationToken cancellationToken = default) =>
            processVerifier.VerifyProcessAsync(claims, session, cancellationToken);
    }

    private sealed class ScriptedBoundaryVerifier(
        IdentityEvidenceSourceTrust sourceTrust,
        Func<CallerSessionIdentityClaims, IdentityVerificationResult<VerifiedSessionIdentity>> onVerifySession,
        Func<CallerProcessIdentityClaims, VerifiedSessionIdentity, IdentityVerificationResult<VerifiedProcessIdentity>> onVerifyProcess)
        : IIdentityBoundaryVerifier
    {
        public IdentityEvidenceSourceTrust SourceTrust { get; } = sourceTrust;

        public Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
            CallerSessionIdentityClaims claims,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(onVerifySession(claims));

        public Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
            CallerProcessIdentityClaims claims,
            VerifiedSessionIdentity session,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(onVerifyProcess(claims, session));
    }

    private sealed class StubWindowsProcessProbe : WindowsProcessIdentityVerifier.IWindowsProcessProbe
    {
        public DateTimeOffset StartUtc { get; init; } = BaseTimeUtc.AddSeconds(-5);
        public string UserSid { get; init; } = InteractiveSid;
        public uint TokenSessionId { get; init; } = 1;
        public uint PidSessionId { get; init; } = 1;

        public bool TryOpenProcess(int processId, out WindowsProcessIdentityVerifier.IProcessHandle? handle, out int win32Error)
        {
            handle = new DummyHandle();
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetProcessTimes(
            WindowsProcessIdentityVerifier.IProcessHandle handle,
            out DateTimeOffset creationTimeUtc,
            out bool hasExitTime,
            out int win32Error)
        {
            creationTimeUtc = StartUtc;
            hasExitTime = false;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetExitCode(WindowsProcessIdentityVerifier.IProcessHandle handle, out uint exitCode, out int win32Error)
        {
            exitCode = WindowsSecurityApis.STILL_ACTIVE;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public uint WaitForExitZero(WindowsProcessIdentityVerifier.IProcessHandle handle, out int win32Error)
        {
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return WindowsSecurityApis.WAIT_TIMEOUT;
        }

        public bool TryOpenProcessToken(
            WindowsProcessIdentityVerifier.IProcessHandle processHandle,
            out WindowsProcessIdentityVerifier.IProcessTokenHandle? tokenHandle,
            out int win32Error)
        {
            tokenHandle = new DummyTokenHandle();
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetTokenUserSid(
            WindowsProcessIdentityVerifier.IProcessTokenHandle tokenHandle,
            out string? sid,
            out int win32Error)
        {
            sid = UserSid;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryGetTokenSessionId(
            WindowsProcessIdentityVerifier.IProcessTokenHandle tokenHandle,
            out uint sessionId,
            out int win32Error)
        {
            sessionId = TokenSessionId;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        public bool TryProcessIdToSessionId(int processId, out uint sessionId, out int win32Error)
        {
            sessionId = PidSessionId;
            win32Error = WindowsSecurityApis.ERROR_SUCCESS;
            return true;
        }

        private sealed class DummyHandle : WindowsProcessIdentityVerifier.IProcessHandle
        {
            public void Dispose() { }
        }

        private sealed class DummyTokenHandle : WindowsProcessIdentityVerifier.IProcessTokenHandle
        {
            public void Dispose() { }
        }
    }
}
