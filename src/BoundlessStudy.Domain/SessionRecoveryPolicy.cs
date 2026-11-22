namespace BoundlessStudy.Domain;

/// <summary>
/// Recovery policy for a <c>RecoverableInterrupted</c> focus session
/// (domain-business-rules.md §5.2, §5.4; A6 approved 2026-11-19). The session is
/// unresolved until the user resumes or discards it, and is abandoned automatically
/// once it has been pending for at least this lifetime.
/// </summary>
public static class SessionRecoveryPolicy
{
    /// <summary>Maximum unresolved interruption age before automatic abandonment.</summary>
    public static readonly TimeSpan PendingInterruptionLifetime = TimeSpan.FromHours(24);
}
