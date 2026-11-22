using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence.Repositories;

/// <summary>SQLite implementation of <see cref="ILocalProfileRepository"/>.</summary>
public sealed class LocalProfileRepository : ILocalProfileRepository
{
    private readonly BoundlessDbContext _context;

    public LocalProfileRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<LocalProfile?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.LocalProfiles.SingleOrDefaultAsync(p => p.Id == id, cancellationToken);

    public Task<LocalProfile?> FindSingleAsync(CancellationToken cancellationToken = default) =>
        _context.LocalProfiles
            .OrderBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(LocalProfile profile, CancellationToken cancellationToken = default)
    {
        await _context.LocalProfiles.AddAsync(profile, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="ISubjectRepository"/>.</summary>
public sealed class SubjectRepository : ISubjectRepository
{
    private readonly BoundlessDbContext _context;

    public SubjectRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<Subject?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.Subjects.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Subject>> ListAsync(
        Guid localProfileId,
        bool includeArchived,
        CancellationToken cancellationToken = default)
    {
        var query = _context.Subjects.Where(s => s.LocalProfileId == localProfileId);
        if (!includeArchived)
        {
            query = query.Where(s => !s.IsArchived);
        }

        return await query
            .OrderBy(s => s.Name)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(Subject subject, CancellationToken cancellationToken = default)
    {
        await _context.Subjects.AddAsync(subject, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IStudyTaskRepository"/>.</summary>
public sealed class StudyTaskRepository : IStudyTaskRepository
{
    private readonly BoundlessDbContext _context;

    public StudyTaskRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<StudyTask?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.StudyTasks.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);

    public async Task<IReadOnlyList<StudyTask>> ListAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.StudyTasks
            .Where(t => t.LocalProfileId == localProfileId)
            .OrderBy(t => t.IsDeleted)
            .ThenBy(t => t.Status)
            .ThenBy(t => t.DueDateLocal)
            .ThenBy(t => t.CreatedAtUtc)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(StudyTask task, CancellationToken cancellationToken = default)
    {
        await _context.StudyTasks.AddAsync(task, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IFocusProfileRepository"/>.</summary>
public sealed class FocusProfileRepository : IFocusProfileRepository
{
    private readonly BoundlessDbContext _context;

    public FocusProfileRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<FocusProfile?> FindAsync(string code, CancellationToken cancellationToken = default) =>
        _context.FocusProfiles.SingleOrDefaultAsync(p => p.Code == code, cancellationToken);

    public async Task<IReadOnlyList<FocusProfile>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _context.FocusProfiles
            .OrderBy(p => p.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(FocusProfile profile, CancellationToken cancellationToken = default)
    {
        await _context.FocusProfiles.AddAsync(profile, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
