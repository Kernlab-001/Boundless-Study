using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence.Repositories;

/// <summary>SQLite implementation of <see cref="IRewardLedgerRepository"/>.</summary>
public sealed class RewardLedgerRepository : IRewardLedgerRepository
{
    private readonly BoundlessDbContext _context;

    public RewardLedgerRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<RewardLedgerEntry?> FindByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        _context.RewardLedgerEntries.SingleOrDefaultAsync(
            e => e.IdempotencyKey == idempotencyKey,
            cancellationToken);

    public async Task<int> SumSessionXpForLocalDateAsync(
        Guid localProfileId,
        DateOnly awardLocalDate,
        CancellationToken cancellationToken = default)
    {
        return await _context.RewardLedgerEntries
            .Where(e => e.LocalProfileId == localProfileId
                && e.AwardLocalDate == awardLocalDate
                && e.CurrencyCode == CurrencyCode.Xp
                && (e.SourceType == LedgerSourceType.SessionCompletion || e.SourceType == LedgerSourceType.SessionPartial))
            .SumAsync(e => (int?)e.Amount, cancellationToken)
            ?? 0;
    }

    public async Task<long> SumXpAsync(Guid localProfileId, CancellationToken cancellationToken = default)
    {
        return await _context.RewardLedgerEntries
            .Where(e => e.LocalProfileId == localProfileId && e.CurrencyCode == CurrencyCode.Xp)
            .SumAsync(e => (long?)e.Amount, cancellationToken)
            ?? 0;
    }

    public async Task<IReadOnlyList<RewardLedgerEntry>> ListByProfileAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.RewardLedgerEntries
            .Where(e => e.LocalProfileId == localProfileId)
            .OrderByDescending(e => e.AwardedAtUtc)
            .ThenBy(e => e.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(RewardLedgerEntry entry, CancellationToken cancellationToken = default)
    {
        await _context.RewardLedgerEntries.AddAsync(entry, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IAchievementRepository"/>.</summary>
public sealed class AchievementRepository : IAchievementRepository
{
    private readonly BoundlessDbContext _context;

    public AchievementRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<AchievementDefinition?> FindAsync(string code, CancellationToken cancellationToken = default) =>
        _context.AchievementDefinitions.SingleOrDefaultAsync(d => d.Code == code, cancellationToken);

    public async Task<IReadOnlyList<AchievementDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await _context.AchievementDefinitions
            .OrderBy(d => d.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(AchievementDefinition definition, CancellationToken cancellationToken = default)
    {
        await _context.AchievementDefinitions.AddAsync(definition, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IUserAchievementRepository"/>.</summary>
public sealed class UserAchievementRepository : IUserAchievementRepository
{
    private readonly BoundlessDbContext _context;

    public UserAchievementRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<UserAchievement?> FindAsync(
        Guid localProfileId,
        string achievementCode,
        CancellationToken cancellationToken = default) =>
        _context.UserAchievements.SingleOrDefaultAsync(
            a => a.LocalProfileId == localProfileId && a.AchievementCode == achievementCode,
            cancellationToken);

    public async Task<IReadOnlyList<UserAchievement>> ListByProfileAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.UserAchievements
            .Where(a => a.LocalProfileId == localProfileId)
            .OrderBy(a => a.AchievementCode)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(UserAchievement achievement, CancellationToken cancellationToken = default)
    {
        await _context.UserAchievements.AddAsync(achievement, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
