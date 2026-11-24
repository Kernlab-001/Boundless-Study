namespace Boundless.Protection.Engine;

/// <summary>
/// Operating-system-backed identity boundary verifier for Windows. Combines live
/// token session verification, live process handle/token verification, and
/// single-handle executable file path and SHA-256 verification.
/// </summary>
public sealed class WindowsIdentityBoundaryVerifier : IIdentityBoundaryVerifier
{
    private readonly WindowsSessionIdentityVerifier _sessionVerifier;
    private readonly WindowsProcessIdentityVerifier _processVerifier;
    private readonly WindowsExecutableIdentityVerifier _executableVerifier;
    private readonly Func<bool> _isWindows;

    public WindowsIdentityBoundaryVerifier()
        : this(
            new WindowsSessionIdentityVerifier(),
            new WindowsProcessIdentityVerifier(),
            new WindowsExecutableIdentityVerifier(),
            OperatingSystem.IsWindows)
    {
    }

    internal WindowsIdentityBoundaryVerifier(
        WindowsSessionIdentityVerifier sessionVerifier,
        WindowsProcessIdentityVerifier processVerifier,
        WindowsExecutableIdentityVerifier executableVerifier,
        Func<bool>? isWindows = null)
    {
        _sessionVerifier = sessionVerifier ?? throw new ArgumentNullException(nameof(sessionVerifier));
        _processVerifier = processVerifier ?? throw new ArgumentNullException(nameof(processVerifier));
        _executableVerifier = executableVerifier ?? throw new ArgumentNullException(nameof(executableVerifier));
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
    }

    public static WindowsIdentityBoundaryVerifier Default => new WindowsIdentityBoundaryVerifier();

    public IdentityEvidenceSourceTrust SourceTrust => IdentityEvidenceSourceTrust.OperatingSystemVerified;

    public Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
        CallerSessionIdentityClaims claims,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isWindows())
        {
            return Task.FromResult(
                IdentityVerificationResult<VerifiedSessionIdentity>.Unsupported(
                    "platform-unsupported-windows-identity-required"));
        }

        return _sessionVerifier.VerifySessionAsync(claims, cancellationToken);
    }

    public async Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
        CallerProcessIdentityClaims claims,
        VerifiedSessionIdentity session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(claims);
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();

        if (!_isWindows())
        {
            return IdentityVerificationResult<VerifiedProcessIdentity>.Unsupported(
                "platform-unsupported-windows-identity-required");
        }

        var processResult = await _processVerifier
            .VerifyProcessAsync(claims, session, cancellationToken)
            .ConfigureAwait(false);
        if (!processResult.IsTrusted || processResult.Evidence is null)
        {
            return processResult.Status == IdentityVerificationStatus.Verified
                ? IdentityVerificationResult<VerifiedProcessIdentity>.Rejected("process-identity-untrusted")
                : processResult;
        }

        var executableResult = await _executableVerifier
            .VerifyAsync(claims.ExecutablePath, claims.ExecutableSha256, cancellationToken)
            .ConfigureAwait(false);
        if (!executableResult.IsTrusted || executableResult.Evidence is null)
        {
            return MapExecutableFailure(executableResult);
        }

        var processEvidence = processResult.Evidence;
        var executableEvidence = executableResult.Evidence;

        var observedAtUtc = processEvidence.ObservedAtUtc >= executableEvidence.ObservedAtUtc
            ? processEvidence.ObservedAtUtc
            : executableEvidence.ObservedAtUtc;
        if (observedAtUtc < processEvidence.ProcessStartUtc)
            observedAtUtc = processEvidence.ProcessStartUtc;

        var verified = new VerifiedProcessIdentity(
            processEvidence.ProcessId,
            processEvidence.ProcessStartUtc,
            processEvidence.SubjectSid,
            executableEvidence.ExecutablePath,
            executableEvidence.Sha256,
            processEvidence.SessionId,
            processEvidence.SessionGeneration,
            observedAtUtc);

        return IdentityVerificationResult<VerifiedProcessIdentity>.Verified(verified);
    }

    private static IdentityVerificationResult<VerifiedProcessIdentity> MapExecutableFailure(
        IdentityVerificationResult<VerifiedExecutableIdentity> executableResult) =>
        executableResult.Status switch
        {
            IdentityVerificationStatus.Unavailable =>
                IdentityVerificationResult<VerifiedProcessIdentity>.Unavailable(executableResult.ReasonCode),
            IdentityVerificationStatus.Unsupported =>
                IdentityVerificationResult<VerifiedProcessIdentity>.Unsupported(executableResult.ReasonCode),
            _ => IdentityVerificationResult<VerifiedProcessIdentity>.Rejected(executableResult.ReasonCode)
        };
}
