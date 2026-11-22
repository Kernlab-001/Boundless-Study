using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.EventRecording;

/// <summary>
/// Appends session events with the per-session monotonic sequence managed through
/// the repository (conceptual-data-model.md §4.7). Events record state transitions
/// and diagnostics; they are never a substitute for checkpoints.
/// </summary>
public sealed class SessionEventRecorder
{
    private readonly ISessionEventRepository _events;
    private readonly ILocalClock _clock;

    public SessionEventRecorder(ISessionEventRepository events, ILocalClock clock)
    {
        _events = events;
        _clock = clock;
    }

    public Task<SessionEvent> RecordAsync(
        SessionType sessionType,
        Guid sessionId,
        SessionEventType eventType,
        string payloadJson = "",
        CancellationToken cancellationToken = default)
    {
        return RecordAtAsync(_clock.UtcNow, sessionType, sessionId, eventType, payloadJson, cancellationToken);
    }

    /// <summary>Reads the event trail for the caller's transaction-aware idempotency checks.</summary>
    public Task<IReadOnlyList<SessionEvent>> ListAsync(
        SessionType sessionType,
        Guid sessionId,
        CancellationToken cancellationToken = default) =>
        _events.ListBySessionAsync(sessionType, sessionId, cancellationToken);

    public async Task<SessionEvent> RecordAtAsync(
        DateTime occurredAtUtc,
        SessionType sessionType,
        Guid sessionId,
        SessionEventType eventType,
        string payloadJson = "",
        CancellationToken cancellationToken = default)
    {
        var sequence = await _events.MaxSequenceAsync(sessionType, sessionId, cancellationToken) + 1;
        var localDate = _clock.ToLocalDate(occurredAtUtc);
        var sessionEvent = SessionEvent.Create(
            sessionType,
            sessionId,
            sequence,
            eventType,
            occurredAtUtc,
            localDate,
            _clock.UtcOffsetMinutes,
            payloadJson,
            occurredAtUtc);
        await _events.AddAsync(sessionEvent, cancellationToken);
        return sessionEvent;
    }
}
