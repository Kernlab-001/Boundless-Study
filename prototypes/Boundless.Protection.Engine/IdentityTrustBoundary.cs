namespace Boundless.Protection.Engine;

/// <summary>
/// Claims supplied by a caller. These values are hints to compare with an
/// authenticated identity; they are never proof of account or process identity.
/// </summary>
public sealed record CallerSessionIdentityClaims(
    Guid SessionId,
    long SessionGeneration,
    string SubjectSid,
    string? AuthenticationContext = null);

/// <summary>
/// Caller-supplied process claims. PID alone is deliberately insufficient: a
/// verifier must bind the PID to the process start instant and revalidate the
/// same live process object/handle before returning trusted evidence.
/// </summary>
public sealed record CallerProcessIdentityClaims(
    int ProcessId,
    DateTimeOffset ProcessStartUtc,
    string SubjectSid,
    string ExecutablePath,
    string ExecutableSha256);

public enum IdentityEvidenceSourceTrust
{
    OperatingSystemVerified,
    CallerSupplied,
    SimulatedUntrusted
}

public enum IdentityVerificationStatus
{
    Verified,
    Rejected,
    Unavailable,
    Unsupported
}

/// <summary>
/// Evidence for a session authenticated by a future trusted verifier. Its
/// constructor is internal so callers cannot turn claims into trusted evidence.
/// Generation is allocated by the trusted session authority and must increase
/// whenever a logical session ID is reused or reauthenticated.
/// </summary>
public sealed class VerifiedSessionIdentity
{
    internal VerifiedSessionIdentity(
        Guid sessionId,
        long generation,
        string subjectSid,
        string authenticationContext,
        DateTimeOffset authenticatedAtUtc)
    {
        SessionId = sessionId;
        Generation = generation;
        SubjectSid = subjectSid;
        AuthenticationContext = authenticationContext;
        AuthenticatedAtUtc = authenticatedAtUtc;
    }

    public Guid SessionId { get; }
    public long Generation { get; }
    public string SubjectSid { get; }
    public string AuthenticationContext { get; }
    public DateTimeOffset AuthenticatedAtUtc { get; }
}

/// <summary>
/// Evidence bound to one live process instance and an authenticated session.
/// A PID reused after process exit is a different identity, even if the image,
/// SID, and PID happen to match. Start time must be read from the OS process
/// instance and checked again against that same instance before acceptance.
/// </summary>
public sealed class VerifiedProcessIdentity
{
    internal VerifiedProcessIdentity(
        int processId,
        DateTimeOffset processStartUtc,
        string subjectSid,
        string executablePath,
        string executableSha256,
        Guid sessionId,
        long sessionGeneration,
        DateTimeOffset observedAtUtc)
    {
        ProcessId = processId;
        ProcessStartUtc = processStartUtc;
        SubjectSid = subjectSid;
        ExecutablePath = executablePath;
        ExecutableSha256 = executableSha256;
        SessionId = sessionId;
        SessionGeneration = sessionGeneration;
        ObservedAtUtc = observedAtUtc;
    }

    public int ProcessId { get; }
    public DateTimeOffset ProcessStartUtc { get; }
    public string SubjectSid { get; }
    public string ExecutablePath { get; }
    public string ExecutableSha256 { get; }
    public Guid SessionId { get; }
    public long SessionGeneration { get; }
    public DateTimeOffset ObservedAtUtc { get; }
}

/// <summary>
/// Result envelope for an identity boundary. Only verifier code in this
/// assembly can create a Verified result containing evidence. Rejected,
/// unavailable, unsupported, caller-supplied, and simulated evidence must fail
/// closed wherever trusted identity is required.
/// </summary>
public sealed class IdentityVerificationResult<TEvidence> where TEvidence : class
{
    private IdentityVerificationResult(
        IdentityVerificationStatus status,
        IdentityEvidenceSourceTrust sourceTrust,
        TEvidence? evidence,
        string reasonCode)
    {
        Status = status;
        SourceTrust = sourceTrust;
        Evidence = evidence;
        ReasonCode = reasonCode;
    }

    public IdentityVerificationStatus Status { get; }
    public IdentityEvidenceSourceTrust SourceTrust { get; }
    public TEvidence? Evidence { get; }
    public string ReasonCode { get; }
    public bool IsTrusted => Status == IdentityVerificationStatus.Verified
        && SourceTrust == IdentityEvidenceSourceTrust.OperatingSystemVerified
        && Evidence is not null;

    internal static IdentityVerificationResult<TEvidence> Verified(TEvidence evidence) =>
        new(IdentityVerificationStatus.Verified, IdentityEvidenceSourceTrust.OperatingSystemVerified,
            evidence ?? throw new ArgumentNullException(nameof(evidence)), "identity-verified");

    internal static IdentityVerificationResult<TEvidence> Rejected(string reasonCode) =>
        new(IdentityVerificationStatus.Rejected, IdentityEvidenceSourceTrust.OperatingSystemVerified,
            null, reasonCode);

    internal static IdentityVerificationResult<TEvidence> Unavailable(string reasonCode) =>
        new(IdentityVerificationStatus.Unavailable, IdentityEvidenceSourceTrust.OperatingSystemVerified,
            null, reasonCode);

    internal static IdentityVerificationResult<TEvidence> Unsupported(string reasonCode) =>
        new(IdentityVerificationStatus.Unsupported, IdentityEvidenceSourceTrust.OperatingSystemVerified,
            null, reasonCode);

    /// <summary>Simulated evidence is explicitly untrusted and never carries verified evidence.</summary>
    public static IdentityVerificationResult<TEvidence> Simulated(string reasonCode) =>
        new(IdentityVerificationStatus.Rejected, IdentityEvidenceSourceTrust.SimulatedUntrusted,
            null, reasonCode);
}

/// <summary>
/// Contract for a future platform verifier. This project supplies no OS-backed
/// implementation. Implementations must authenticate session identity from an
/// OS-authoritative security context, compare caller claims, and bind process
/// evidence to the same live process instance. A simulator or caller-fed source
/// cannot satisfy this contract and must be rejected for trusted decisions.
/// This is a specification-only interface: verified evidence issuance is internal,
/// so any future implementation must remain within this assembly or use a separately
/// reviewed, controlled evidence-issuance boundary. This verifier is not wired into
/// ProtectionPolicyEvaluator in this milestone.
/// </summary>
public interface IIdentityBoundaryVerifier
{
    IdentityEvidenceSourceTrust SourceTrust { get; }

    Task<IdentityVerificationResult<VerifiedSessionIdentity>> VerifySessionAsync(
        CallerSessionIdentityClaims claims,
        CancellationToken cancellationToken = default);

    Task<IdentityVerificationResult<VerifiedProcessIdentity>> VerifyProcessAsync(
        CallerProcessIdentityClaims claims,
        VerifiedSessionIdentity session,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Shared trust gate for consumers. It intentionally rejects simulated sources,
/// incomplete results, and mismatched session generations/SIDs.
/// </summary>
public static class IdentityTrustBoundary
{
    public static bool IsTrustedSession(
        IIdentityBoundaryVerifier verifier,
        IdentityVerificationResult<VerifiedSessionIdentity>? result) =>
        verifier is not null
        && verifier.SourceTrust == IdentityEvidenceSourceTrust.OperatingSystemVerified
        && result is { IsTrusted: true, Evidence: not null };

    public static bool IsTrustedProcess(
        IIdentityBoundaryVerifier verifier,
        IdentityVerificationResult<VerifiedProcessIdentity>? result,
        VerifiedSessionIdentity? session) =>
        verifier is not null
        && verifier.SourceTrust == IdentityEvidenceSourceTrust.OperatingSystemVerified
        && result is { IsTrusted: true, Evidence: not null }
        && session is not null
        && result.Evidence.SessionId == session.SessionId
        && result.Evidence.SessionGeneration == session.Generation
        && string.Equals(result.Evidence.SubjectSid, session.SubjectSid, StringComparison.OrdinalIgnoreCase);
}
