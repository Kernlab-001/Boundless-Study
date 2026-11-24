using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Boundless.Protection.Engine;

namespace Boundless.Protection.Engine.Tests;

public sealed class WindowsSessionIdentityVerifierTests
{
    private const string SyntheticSid = "S-1-5-21-111111111-222222222-333333333-1001";
    private const string SyntheticLuid = "0x1234abcd";

    [Fact]
    public void ReadCurrentProcessTokenIdentity_OnWindows_ReadsUserSidAuthenticationIdAndSessionId()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var ok = WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var identity, out var error);

        Assert.True(ok, error);
        Assert.Equal(string.Empty, error);
        Assert.Matches(new Regex(@"^S-1-(?:\d+-){1,14}\d+$", RegexOptions.CultureInvariant), identity.UserSid);
        Assert.StartsWith("0x", identity.AuthenticationId, StringComparison.Ordinal);
        Assert.True(ulong.TryParse(identity.AuthenticationId[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _));
        Assert.Equal((uint)Process.GetCurrentProcess().SessionId, identity.SessionId);
    }

    [Fact]
    public async Task VerifySessionAsync_WithMatchingRealOsClaims_ReturnsVerifiedEvidence()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var sessionId = Guid.NewGuid();
        var verifier = new WindowsSessionIdentityVerifier();
        var claims = new CallerSessionIdentityClaims(
            sessionId,
            SessionGeneration: 3,
            SubjectSid: osIdentity.UserSid,
            AuthenticationContext: osIdentity.AuthenticationId);

        var result = await verifier.VerifySessionAsync(claims);

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.OperatingSystemVerified, result.SourceTrust);
        Assert.True(result.IsTrusted);
        Assert.Equal("identity-verified", result.ReasonCode);
        Assert.NotNull(result.Evidence);
        Assert.Equal(sessionId, result.Evidence.SessionId);
        Assert.Equal(3, result.Evidence.Generation);
        Assert.Equal(osIdentity.UserSid, result.Evidence.SubjectSid);
        Assert.Equal(osIdentity.AuthenticationId, result.Evidence.AuthenticationContext);
        Assert.Equal(TimeSpan.Zero, result.Evidence.AuthenticatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, result.Evidence.StartsAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, result.Evidence.ExpiresAtUtc.Offset);
        Assert.True(result.Evidence.StartsAtUtc < result.Evidence.ExpiresAtUtc);
    }

    [Fact]
    public async Task VerifySessionAsync_WithoutSuppliedAuthContext_PopulatesOsLogonLuid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var verifier = new WindowsSessionIdentityVerifier();
        var claims = new CallerSessionIdentityClaims(Guid.NewGuid(), 1, osIdentity.UserSid);

        var result = await verifier.VerifySessionAsync(claims);

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.True(result.IsTrusted);
        Assert.Equal(osIdentity.AuthenticationId, result.Evidence!.AuthenticationContext);
    }

    [Fact]
    public async Task VerifySessionAsync_AgainstRealOsToken_RejectsMismatchedSubjectSid()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var mismatchedSid = string.Equals(osIdentity.UserSid, SyntheticSid, StringComparison.Ordinal)
            ? "S-1-5-21-111111111-222222222-333333333-1002"
            : SyntheticSid;
        var verifier = new WindowsSessionIdentityVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, mismatchedSid, osIdentity.AuthenticationId));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("session-subject-sid-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifySessionAsync_AgainstRealOsToken_RejectsMismatchedAuthContext()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Assert.True(WindowsSessionIdentityVerifier.TryReadCurrentProcessTokenIdentity(out var osIdentity, out var error), error);
        var mismatchedLuid = string.Equals(osIdentity.AuthenticationId, "0xdeadbeefdeadbeef", StringComparison.Ordinal)
            ? "0xcafebabecafebabe"
            : "0xdeadbeefdeadbeef";
        var verifier = new WindowsSessionIdentityVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, osIdentity.UserSid, mismatchedLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("session-auth-context-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifySessionAsync_WhenNotWindows_ReturnsUnsupported()
    {
        var verifier = CreateVerifier(isWindows: false);

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, SyntheticSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Unsupported, result.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.OperatingSystemVerified, result.SourceTrust);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("platform-unsupported-windows-identity-required", result.ReasonCode);
    }

    [Fact]
    public async Task VerifySessionAsync_WithEmptySessionId_ReturnsRejected()
    {
        var verifier = CreateVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.Empty, 1, SyntheticSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("invalid-session-id", result.ReasonCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public async Task VerifySessionAsync_WithNonPositiveGeneration_ReturnsRejected(long generation)
    {
        var verifier = CreateVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), generation, SyntheticSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("invalid-session-generation", result.ReasonCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("forged-sid")]
    [InlineData("S-1-5-21-111111111-222222222-333333333-1002")]
    [InlineData("s-1-5-21-111111111-222222222-333333333-1001")]
    public async Task VerifySessionAsync_WithMismatchedSubjectSid_ReturnsRejected(string claimedSid)
    {
        var verifier = CreateVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, claimedSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("session-subject-sid-mismatch", result.ReasonCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0x9999")]
    [InlineData("0X1234ABCD")]
    public async Task VerifySessionAsync_WithMismatchedAuthenticationContext_ReturnsRejected(string claimedAuthContext)
    {
        var verifier = CreateVerifier();

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, SyntheticSid, claimedAuthContext));

        Assert.Equal(IdentityVerificationStatus.Rejected, result.Status);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("session-auth-context-mismatch", result.ReasonCode);
    }

    [Fact]
    public async Task VerifySessionAsync_WhenOsTokenCallFails_ReturnsUnavailableWithError()
    {
        var verifier = CreateVerifier(
            tokenSucceeds: false,
            tokenError: "OpenProcessToken failed with Win32 error 5");

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, SyntheticSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.Equal(IdentityEvidenceSourceTrust.OperatingSystemVerified, result.SourceTrust);
        Assert.False(result.IsTrusted);
        Assert.Null(result.Evidence);
        Assert.Equal("os-identity-unavailable: OpenProcessToken failed with Win32 error 5", result.ReasonCode);
    }

    [Fact]
    public async Task VerifySessionAsync_WhenOsTokenReturnsEmptyAttributes_ReturnsUnavailable()
    {
        var verifier = new WindowsSessionIdentityVerifier(
            () => true,
            (out WindowsTokenSessionIdentity identity, out string error) =>
            {
                identity = new WindowsTokenSessionIdentity(string.Empty, SyntheticLuid, 1);
                error = string.Empty;
                return true;
            },
            () => DateTimeOffset.UtcNow);

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 1, SyntheticSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Unavailable, result.Status);
        Assert.StartsWith("os-identity-unavailable:", result.ReasonCode, StringComparison.Ordinal);
        Assert.Null(result.Evidence);
    }

    [Fact]
    public async Task VerifySessionAsync_NormalizesNonZeroOffsetClockToUtc()
    {
        var localNow = new DateTimeOffset(2026, 11, 24, 18, 0, 0, TimeSpan.FromHours(5.5));
        var verifier = new WindowsSessionIdentityVerifier(
            () => true,
            (out WindowsTokenSessionIdentity identity, out string error) =>
            {
                identity = new WindowsTokenSessionIdentity(SyntheticSid, SyntheticLuid, 1);
                error = string.Empty;
                return true;
            },
            () => localNow,
            TimeSpan.FromHours(4));

        var result = await verifier.VerifySessionAsync(
            new CallerSessionIdentityClaims(Guid.NewGuid(), 2, SyntheticSid, SyntheticLuid));

        Assert.Equal(IdentityVerificationStatus.Verified, result.Status);
        Assert.NotNull(result.Evidence);
        Assert.Equal(TimeSpan.Zero, result.Evidence.AuthenticatedAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, result.Evidence.StartsAtUtc.Offset);
        Assert.Equal(TimeSpan.Zero, result.Evidence.ExpiresAtUtc.Offset);
        Assert.Equal(localNow.ToUniversalTime(), result.Evidence.AuthenticatedAtUtc);
        Assert.Equal(localNow.ToUniversalTime().AddHours(4), result.Evidence.ExpiresAtUtc);
    }

    [Fact]
    public async Task VerifySessionAsync_WhenClaimsNull_ThrowsArgumentNullException()
    {
        var verifier = CreateVerifier();

        await Assert.ThrowsAsync<ArgumentNullException>(() => verifier.VerifySessionAsync(null!));
    }

    [Fact]
    public async Task VerifySessionAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        var verifier = CreateVerifier();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            verifier.VerifySessionAsync(
                new CallerSessionIdentityClaims(Guid.NewGuid(), 1, SyntheticSid, SyntheticLuid),
                cts.Token));
    }

    private static WindowsSessionIdentityVerifier CreateVerifier(
        bool isWindows = true,
        bool tokenSucceeds = true,
        string tokenError = "")
    {
        return new WindowsSessionIdentityVerifier(
            () => isWindows,
            (out WindowsTokenSessionIdentity identity, out string error) =>
            {
                if (!tokenSucceeds)
                {
                    identity = default;
                    error = tokenError;
                    return false;
                }

                identity = new WindowsTokenSessionIdentity(SyntheticSid, SyntheticLuid, 1);
                error = string.Empty;
                return true;
            },
            () => DateTimeOffset.UtcNow);
    }
}
