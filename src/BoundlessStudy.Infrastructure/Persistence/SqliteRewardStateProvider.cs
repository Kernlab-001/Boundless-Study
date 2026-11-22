using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Derived reward counts. Every value is computed from primary rows inside the
/// current transaction; no cache is used as a source of truth (§5, §8.9).
/// </summary>
public sealed class SqliteRewardStateProvider : IRewardStateProvider
{
    private readonly BoundlessDbContext _context;

    public SqliteRewardStateProvider(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<int> GetQualifiedSessionCountAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default) =>
        _context.FocusSessions.CountAsync(
            s => s.LocalProfileId == localProfileId
                && s.State == SessionState.Completed
                && s.ActiveStudySeconds >= EconomyRules.QualifiedSessionMinimumActiveSeconds,
            cancellationToken);

    public Task<int> GetStrictQualifiedSessionCountAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default) =>
        _context.FocusSessions.CountAsync(
            s => s.LocalProfileId == localProfileId
                && s.ProfileCode == BuiltInProfiles.StrictCode
                && s.State == SessionState.Completed
                && s.ActiveStudySeconds >= EconomyRules.QualifiedSessionMinimumActiveSeconds,
            cancellationToken);

    public async Task<int> GetTotalActiveStudyMinutesAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        var seconds = await _context.FocusSessions
            .Where(s => s.LocalProfileId == localProfileId
                && (s.State == SessionState.Completed
                    || s.State == SessionState.EndedEarly
                    || s.State == SessionState.Abandoned))
            .SumAsync(s => (long?)s.ActiveStudySeconds, cancellationToken)
            ?? 0;

        return (int)(seconds / 60);
    }

    public Task<int> GetCompletedTaskCountAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default) =>
        _context.StudyTasks.CountAsync(
            t => t.LocalProfileId == localProfileId
                && t.CompletedAtUtc != null,
            cancellationToken);

    public async Task<IReadOnlyList<DateOnly>> ListQualifiedLocalDatesAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        // A qualified date comes from the recorded Completed event's LocalDateAtEvent,
        // not from wall-clock elapsed time or the ledger (§8.8). RewardCreated is the
        // fallback for historical/partial-event paths; Completed wins when both exist.
        var rows = await _context.SessionEvents
            .AsNoTracking()
            .Where(e => e.SessionType == SessionType.Focus
                && (e.EventType == SessionEventType.Completed || e.EventType == SessionEventType.RewardCreated)
                && _context.FocusSessions.Any(s =>
                    s.Id == e.SessionId
                    && s.LocalProfileId == localProfileId
                    && s.State == SessionState.Completed
                    && s.ActiveStudySeconds >= EconomyRules.QualifiedSessionMinimumActiveSeconds))
            .Select(e => new { e.SessionId, e.EventType, e.Sequence, e.LocalDateAtEvent })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.SessionId)
            .Select(g => g
                .OrderByDescending(r => r.EventType == SessionEventType.Completed)
                .ThenByDescending(r => r.Sequence)
                .First()
                .LocalDateAtEvent)
            .Distinct()
            .OrderBy(d => d)
            .ToArray();
    }
}
