using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence.Repositories;

/// <summary>SQLite implementation of <see cref="IAppSettingRepository"/>.</summary>
public sealed class AppSettingRepository : IAppSettingRepository
{
    private readonly BoundlessDbContext _context;

    public AppSettingRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<AppSetting?> FindAsync(string key, CancellationToken cancellationToken = default) =>
        _context.AppSettings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken);

    public async Task AddAsync(AppSetting setting, CancellationToken cancellationToken = default)
    {
        await _context.AppSettings.AddAsync(setting, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IBackupRecordRepository"/>.</summary>
public sealed class BackupRecordRepository : IBackupRecordRepository
{
    private readonly BoundlessDbContext _context;

    public BackupRecordRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public async Task<IReadOnlyList<BackupRecord>> ListAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BackupRecords
            .Where(r => r.LocalProfileId == localProfileId)
            .OrderByDescending(r => r.CreatedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(BackupRecord record, CancellationToken cancellationToken = default)
    {
        await _context.BackupRecords.AddAsync(record, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
