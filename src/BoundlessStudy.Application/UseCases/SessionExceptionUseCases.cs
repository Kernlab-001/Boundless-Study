using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Grants a temporary, session-scoped exception (§7.3). The profile fixes the count
/// and duration (Flexible: not applicable; Balanced: 2 × 5 min; Strict: 1 × 2 min
/// after a 30-second delay). Expiry never removes the underlying block rule.
/// </summary>
public sealed class GrantSessionExceptionHandler
{
    private readonly IFocusSessionRepository _sessions;
    private readonly IFocusProfileRepository _focusProfiles;
    private readonly ISessionExceptionRepository _exceptions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public GrantSessionExceptionHandler(
        IFocusSessionRepository sessions,
        IFocusProfileRepository focusProfiles,
        ISessionExceptionRepository exceptions,
        IUnitOfWork unitOfWork,
        ILocalClock clock,
        SessionEventRecorder events)
    {
        _sessions = sessions;
        _focusProfiles = focusProfiles;
        _exceptions = exceptions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
    }

    public async Task<EntityResult> HandleAsync(
        Guid focusSessionId,
        ExceptionTargetType targetType,
        string targetId,
        ExceptionReasonCategory reasonCategory,
        CancellationToken cancellationToken = default)
    {
        var session = await _sessions.FindAsync(focusSessionId, cancellationToken);
        if (session is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (!session.IsActive || session.State is SessionState.RecoverableInterrupted)
        {
            return EntityResult.Fail(UseCaseErrorCodes.SessionNotActive, "Exceptions can be granted only while the session is active.");
        }

        var profile = await _focusProfiles.FindAsync(session.ProfileCode, cancellationToken);
        if (profile is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.FocusProfileNotFound, "The session's focus profile no longer exists.");
        }

        var grantedCount = await _exceptions.CountBySessionAsync(focusSessionId, cancellationToken);
        if (grantedCount >= profile.MaxExceptionsPerSession)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ExceptionLimitReached,
                $"The {profile.DisplayName} profile allows at most {profile.MaxExceptionsPerSession} exception(s) per session.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var now = _clock.UtcNow;
                    var activationAt = now.AddSeconds(profile.ExceptionDelaySeconds);
                    var expiresAt = activationAt.AddSeconds(profile.ExceptionDurationSeconds);
                    var exception = SessionException.Create(
                        focusSessionId,
                        targetType,
                        targetId,
                        now,
                        expiresAt,
                        reasonCategory,
                        now);
                    await _exceptions.AddAsync(exception, ct);
                    await _events.RecordAsync(
                        SessionType.Focus,
                        focusSessionId,
                        SessionEventType.ExceptionGranted,
                        $"{{\"targetType\":\"{targetType}\",\"expiresAtUtc\":\"{expiresAtUtc(expiresAt)}\"}}",
                        ct);
                    return EntityResult.Ok(exception.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }

    private static string expiresAtUtc(DateTime expiresAtUtc) =>
        expiresAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
}
