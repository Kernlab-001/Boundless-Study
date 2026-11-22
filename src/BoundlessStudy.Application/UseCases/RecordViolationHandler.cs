using System.Text.Json;
using System.Text.RegularExpressions;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Command object for recording violation and enforcement events.
/// </summary>
public sealed record RecordViolationCommand(
    Guid SessionId,
    SessionEventType EventType,
    string TargetId,
    string? TargetType = null,
    TimeSpan? Duration = null,
    int? ViolationCount = null,
    string? Reason = null,
    string? CustomPayloadJson = null);

/// <summary>
/// Records blocking violations and enforcement events (conceptual-data-model.md §4.7,
/// domain-business-rules.md §7.1–§7.3, functional-requirements.md BS-APP-008).
/// Enforces:
/// 1. Transactional safety via CommandBoundary.
/// 2. Validates event types: Blocked, Unblocked, EnforcementFailed, ExceptionGranted.
/// 3. Validates payload JSON size <= 2,000 characters (SessionEvent.MaxPayloadJsonLength).
/// 4. Strict privacy (BS-APP-008): Rejects window titles, browsing URLs with paths/queries,
///    document content, and personal data. Sanitizes target identifiers.
/// </summary>
public sealed class RecordViolationHandler
{
    private static readonly HashSet<SessionEventType> SupportedEventTypes =
    [
        SessionEventType.Blocked,
        SessionEventType.Unblocked,
        SessionEventType.EnforcementFailed,
        SessionEventType.ExceptionGranted,
    ];

    private static readonly HashSet<string> ProhibitedJsonKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowtitle",
        "window_title",
        "title",
        "url",
        "fullurl",
        "full_url",
        "browsingurl",
        "browsing_url",
        "document",
        "documentcontent",
        "content",
        "pagecontent",
        "page_content",
        "querystring",
        "query",
        "cookie",
        "password",
        "token",
        "personaldata",
    };

    private static readonly Regex UrlWithPathRegex = new(
        @"https?://[^\s/$.?#].[^\s]*/[^\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IFocusSessionRepository _sessions;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;
    private readonly SessionEventRecorder _events;

    public RecordViolationHandler(
        IFocusSessionRepository sessions,
        IUnitOfWork unitOfWork,
        ILocalClock clock,
        SessionEventRecorder events)
    {
        _sessions = sessions;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _events = events;
    }

    public Task<UseCaseResult> HandleAsync(
        Guid sessionId,
        SessionEventType eventType,
        string targetId,
        string? targetType = null,
        string? payloadJson = null,
        CancellationToken cancellationToken = default)
    {
        return HandleAsync(new RecordViolationCommand(
            sessionId,
            eventType,
            targetId,
            targetType,
            CustomPayloadJson: payloadJson), cancellationToken);
    }

    public async Task<UseCaseResult> HandleAsync(
        RecordViolationCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (!SupportedEventTypes.Contains(command.EventType))
        {
            return UseCaseResult.Fail(
                UseCaseErrorCodes.DomainRuleViolation,
                $"Event type '{command.EventType}' is not a supported violation or enforcement event.");
        }

        if (string.IsNullOrWhiteSpace(command.TargetId))
        {
            return UseCaseResult.Fail(
                UseCaseErrorCodes.ValidationFailed,
                "A violation or enforcement event must specify a target identifier.");
        }

        // BS-APP-008: Sanitize target identifier (strip full directory paths and URL paths/queries)
        var sanitizedTarget = SanitizeTarget(command.TargetId);
        if (string.IsNullOrWhiteSpace(sanitizedTarget))
        {
            return UseCaseResult.Fail(
                UseCaseErrorCodes.ValidationFailed,
                "Sanitized target identifier cannot be empty.");
        }

        // Validate custom payload if provided
        string finalPayloadJson;
        if (!string.IsNullOrEmpty(command.CustomPayloadJson))
        {
            if (command.CustomPayloadJson.Length > SessionEvent.MaxPayloadJsonLength)
            {
                return UseCaseResult.Fail(
                    "EVENT_PAYLOAD_TOO_LARGE",
                    $"A session event payload may be at most {SessionEvent.MaxPayloadJsonLength} characters.");
            }

            var privacyCheck = ValidatePrivacy(command.CustomPayloadJson);
            if (!privacyCheck.Success)
            {
                return privacyCheck;
            }

            finalPayloadJson = command.CustomPayloadJson;
        }
        else
        {
            var payloadObj = new Dictionary<string, object?>
            {
                ["targetId"] = sanitizedTarget,
                ["targetType"] = command.TargetType ?? "Target",
                ["occurredAtUtc"] = _clock.UtcNow.ToString("o"),
            };

            if (command.Duration.HasValue)
            {
                payloadObj["durationSeconds"] = (int)command.Duration.Value.TotalSeconds;
            }

            if (command.ViolationCount.HasValue)
            {
                payloadObj["violationCount"] = command.ViolationCount.Value;
            }

            if (!string.IsNullOrWhiteSpace(command.Reason))
            {
                payloadObj["reason"] = command.Reason;
            }

            finalPayloadJson = JsonSerializer.Serialize(payloadObj);
            if (finalPayloadJson.Length > SessionEvent.MaxPayloadJsonLength)
            {
                return UseCaseResult.Fail(
                    "EVENT_PAYLOAD_TOO_LARGE",
                    $"A session event payload may be at most {SessionEvent.MaxPayloadJsonLength} characters.");
            }
        }

        var session = await _sessions.FindAsync(command.SessionId, cancellationToken);
        if (session is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotFound, "The focus session does not exist.");
        }

        if (!session.IsActive && session.State != SessionState.Blocked && session.State != SessionState.SystemPaused)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SessionNotActive, "Violations can only be recorded during an active or paused session.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var now = _clock.UtcNow;

                    // Update session state and counters depending on event type
                    switch (command.EventType)
                    {
                        case SessionEventType.Blocked:
                            // A website redirect is a successful intervention but does not
                            // place the desktop behind an application overlay. Keep the
                            // focus state running while recording the violation separately.
                            if (!string.Equals(command.TargetType, "Website", StringComparison.OrdinalIgnoreCase)
                                && session.State == SessionState.Active)
                            {
                                session.Block(now);
                            }
                            // Increment violation count on session checkpoint
                            session.ApplyCheckpoint(
                                now,
                                session.ElapsedSeconds,
                                session.ActiveStudySeconds,
                                session.IdleSeconds,
                                session.ProtectionGapSeconds,
                                session.ViolationCount + 1,
                                session.ExceptionCount,
                                session.RecoveryCount);
                            break;

                        case SessionEventType.Unblocked:
                            if (session.State == SessionState.Blocked)
                            {
                                session.Unblock(now);
                            }
                            break;

                        case SessionEventType.ExceptionGranted:
                            // Increment exception count on session checkpoint
                            session.ApplyCheckpoint(
                                now,
                                session.ElapsedSeconds,
                                session.ActiveStudySeconds,
                                session.IdleSeconds,
                                session.ProtectionGapSeconds,
                                session.ViolationCount,
                                session.ExceptionCount + 1,
                                session.RecoveryCount);
                            break;

                        case SessionEventType.EnforcementFailed:
                            // Enforcement failed diagnostic recorded without counter increment
                            break;
                    }

                    await _events.RecordAtAsync(
                        now,
                        SessionType.Focus,
                        session.Id,
                        command.EventType,
                        finalPayloadJson,
                        ct);

                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }

    /// <summary>
    /// Enforces BS-APP-008 strict privacy: inspects JSON keys and contents to ensure
    /// no window titles, URLs with paths, or personal document content are stored.
    /// </summary>
    private static UseCaseResult ValidatePrivacy(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (ContainsProhibitedData(doc.RootElement))
            {
                return UseCaseResult.Fail(
                    UseCaseErrorCodes.DomainRuleViolation,
                    "Payload contains prohibited personal or browsing data (window titles, URLs, or content) violating BS-APP-008.");
            }
        }
        catch (JsonException)
        {
            return UseCaseResult.Fail(
                UseCaseErrorCodes.ValidationFailed,
                "Payload must be valid JSON.");
        }

        if (UrlWithPathRegex.IsMatch(payloadJson))
        {
            return UseCaseResult.Fail(
                UseCaseErrorCodes.DomainRuleViolation,
                "Payload contains full URL with path or query violating BS-APP-008 privacy policy.");
        }

        return UseCaseResult.Ok();
    }

    private static bool ContainsProhibitedData(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    if (ProhibitedJsonKeys.Contains(prop.Name))
                    {
                        return true;
                    }

                    if (ContainsProhibitedData(prop.Value))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (ContainsProhibitedData(item))
                    {
                        return true;
                    }
                }
                break;

            case JsonValueKind.String:
                var str = element.GetString();
                if (!string.IsNullOrEmpty(str) && UrlWithPathRegex.IsMatch(str))
                {
                    return true;
                }
                break;
        }

        return false;
    }

    /// <summary>
    /// Normalizes target identifier:
    /// - For file paths: extracts file name (e.g. "C:\Path\discord.exe" -> "discord.exe").
    /// - For URLs: extracts host domain (e.g. "https://www.youtube.com/watch?v=123" -> "youtube.com").
    /// </summary>
    private static string SanitizeTarget(string target)
    {
        var trimmed = target.Trim();

        // If it looks like a URL, extract domain only
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                trimmed = uri.Host;
            }
        }

        // If it contains path separators, strip directory components
        if (trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            try
            {
                trimmed = Path.GetFileName(trimmed);
            }
            catch
            {
                // Fall back to manual slash splitting
                var idx = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
                if (idx >= 0 && idx < trimmed.Length - 1)
                {
                    trimmed = trimmed[(idx + 1)..];
                }
            }
        }

        // Strip leading www. if domain
        if (trimmed.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && trimmed.Length > 4)
        {
            trimmed = trimmed[4..];
        }

        return trimmed.ToLowerInvariant();
    }
}
