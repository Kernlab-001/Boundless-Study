namespace BoundlessStudy.Domain.Entities;

/// <summary>
/// Append-only session event record (conceptual-data-model.md §4.7). Insert-only;
/// no updates or deletes through normal operation. Payloads are small, structured,
/// and size-limited; they never contain URLs, window titles, or content.
/// </summary>
public class SessionEvent
{
    /// <summary>
    /// Payload size limit. The approved maximum is 2000 characters (A1, 2026-11-19).
    /// It is enforced here for new events and by the CK_SessionEvent_PayloadJson
    /// database check; existing event history is never rewritten or rejected.
    /// </summary>
    public const int MaxPayloadJsonLength = 2000;

    public Guid Id { get; private set; }

    public SessionType SessionType { get; private set; }

    public Guid SessionId { get; private set; }

    public int Sequence { get; private set; }

    public SessionEventType EventType { get; private set; }

    public DateTime OccurredAtUtc { get; private set; }

    public DateOnly LocalDateAtEvent { get; private set; }

    public int UtcOffsetMinutes { get; private set; }

    public string PayloadJson { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }

    private SessionEvent()
    {
    }

    public static SessionEvent Create(
        SessionType sessionType,
        Guid sessionId,
        int sequence,
        SessionEventType eventType,
        DateTime occurredAtUtc,
        DateOnly localDateAtEvent,
        int utcOffsetMinutes,
        string payloadJson,
        DateTime createdAtUtc)
    {
        if (sequence < 1)
        {
            throw new DomainException("EVENT_SEQUENCE_INVALID", "A session event sequence must be a positive, per-session monotonic number.");
        }

        if (payloadJson is not null && payloadJson.Length > MaxPayloadJsonLength)
        {
            throw new DomainException("EVENT_PAYLOAD_TOO_LARGE", $"A session event payload may be at most {MaxPayloadJsonLength} characters.");
        }

        return new SessionEvent
        {
            Id = Guid.NewGuid(),
            SessionType = sessionType,
            SessionId = sessionId,
            Sequence = sequence,
            EventType = eventType,
            OccurredAtUtc = occurredAtUtc,
            LocalDateAtEvent = localDateAtEvent,
            UtcOffsetMinutes = utcOffsetMinutes,
            PayloadJson = payloadJson ?? string.Empty,
            CreatedAtUtc = createdAtUtc,
        };
    }
}

/// <summary>
/// A temporary, session-scoped exception to one blocked target
/// (conceptual-data-model.md §4.8, domain-business-rules.md §7.3).
/// Expiry never removes the underlying block rule.
/// </summary>
public class SessionException
{
    public Guid Id { get; private set; }

    public Guid FocusSessionId { get; private set; }

    public ExceptionTargetType TargetType { get; private set; }

    public string TargetId { get; private set; } = string.Empty;

    public DateTime GrantedAtUtc { get; private set; }

    public DateTime ExpiresAtUtc { get; private set; }

    public ExceptionReasonCategory ReasonCategory { get; private set; }

    public bool WasAutoExpired { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    private SessionException()
    {
    }

    public static SessionException Create(
        Guid focusSessionId,
        ExceptionTargetType targetType,
        string targetId,
        DateTime grantedAtUtc,
        DateTime expiresAtUtc,
        ExceptionReasonCategory reasonCategory,
        DateTime createdAtUtc)
    {
        if (string.IsNullOrWhiteSpace(targetId))
        {
            throw new DomainException("EXCEPTION_TARGET_REQUIRED", "An exception must name its target.");
        }

        if (targetId.Length > 253)
        {
            throw new DomainException("EXCEPTION_TARGET_TOO_LONG", "An exception target may be at most 253 characters.");
        }

        if (expiresAtUtc <= grantedAtUtc)
        {
            throw new DomainException("EXCEPTION_EXPIRY_INVALID", "An exception must expire after it is granted.");
        }

        return new SessionException
        {
            Id = Guid.NewGuid(),
            FocusSessionId = focusSessionId,
            TargetType = targetType,
            TargetId = targetId,
            GrantedAtUtc = grantedAtUtc,
            ExpiresAtUtc = expiresAtUtc,
            ReasonCategory = reasonCategory,
            CreatedAtUtc = createdAtUtc,
        };
    }

    /// <summary>Set when expiry is processed. Idempotent.</summary>
    public void MarkAutoExpired()
    {
        WasAutoExpired = true;
    }
}
