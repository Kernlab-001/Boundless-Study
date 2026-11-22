using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence.Repositories;

/// <summary>SQLite implementation of <see cref="IFocusSessionRepository"/>.</summary>
public sealed class FocusSessionRepository : IFocusSessionRepository
{
    private static readonly SessionState[] ActiveStates =
    [
        SessionState.Active,
        SessionState.Blocked,
        SessionState.SystemPaused,
        SessionState.RecoverableInterrupted,
    ];

    private readonly BoundlessDbContext _context;

    public FocusSessionRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<FocusSession?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.FocusSessions.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<FocusSession?> FindActiveByProfileAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default) =>
        _context.FocusSessions
            .Where(s => s.LocalProfileId == localProfileId && ActiveStates.Contains(s.State))
            .OrderByDescending(s => s.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public Task<FocusSession?> FindActiveByTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        _context.FocusSessions
            .Where(s => s.TaskId == taskId && ActiveStates.Contains(s.State))
            .OrderByDescending(s => s.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<FocusSession>> ListByProfileAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.FocusSessions
            .Where(s => s.LocalProfileId == localProfileId)
            .OrderByDescending(s => s.StartedAtUtc)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(FocusSession session, CancellationToken cancellationToken = default)
    {
        await _context.FocusSessions.AddAsync(session, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IBreakSessionRepository"/>.</summary>
public sealed class BreakSessionRepository : IBreakSessionRepository
{
    private readonly BoundlessDbContext _context;

    public BreakSessionRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<BreakSession?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.BreakSessions.SingleOrDefaultAsync(b => b.Id == id, cancellationToken);

    public Task<BreakSession?> FindActiveByProfileAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default) =>
        _context.BreakSessions
            .Where(b => b.LocalProfileId == localProfileId && b.State == BreakState.Active)
            .OrderByDescending(b => b.StartedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(BreakSession session, CancellationToken cancellationToken = default)
    {
        await _context.BreakSessions.AddAsync(session, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="ISessionEventRepository"/>.</summary>
public sealed class SessionEventRepository : ISessionEventRepository
{
    private readonly BoundlessDbContext _context;

    public SessionEventRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public async Task<int> MaxSequenceAsync(
        SessionType sessionType,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.SessionEvents
            .Where(e => e.SessionType == sessionType && e.SessionId == sessionId)
            .MaxAsync(e => (int?)e.Sequence, cancellationToken)
            ?? 0;
    }

    public async Task<IReadOnlyList<SessionEvent>> ListBySessionAsync(
        SessionType sessionType,
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.SessionEvents
            .Where(e => e.SessionType == sessionType && e.SessionId == sessionId)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(SessionEvent sessionEvent, CancellationToken cancellationToken = default)
    {
        await _context.SessionEvents.AddAsync(sessionEvent, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="ISessionExceptionRepository"/>.</summary>
public sealed class SessionExceptionRepository : ISessionExceptionRepository
{
    private readonly BoundlessDbContext _context;

    public SessionExceptionRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<int> CountBySessionAsync(Guid focusSessionId, CancellationToken cancellationToken = default) =>
        _context.SessionExceptions.CountAsync(e => e.FocusSessionId == focusSessionId, cancellationToken);

    public async Task<IReadOnlyList<SessionException>> ListBySessionAsync(
        Guid focusSessionId,
        CancellationToken cancellationToken = default)
    {
        return await _context.SessionExceptions
            .Where(e => e.FocusSessionId == focusSessionId)
            .OrderBy(e => e.GrantedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(SessionException exception, CancellationToken cancellationToken = default)
    {
        await _context.SessionExceptions.AddAsync(exception, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
