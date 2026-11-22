using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

public sealed record SeedResult(
    Guid LocalProfileId,
    bool LocalProfileCreated,
    int FocusProfilesCreated,
    int AchievementDefinitionsCreated);

/// <summary>
/// Idempotent seed of the single MVP profile, the three frozen focus profiles
/// (ECONOMY-1 §4.1), and the five approved achievement definitions (§8.9).
/// Existing reference rows are never overwritten: a changed definition requires a
/// new migration or a new achievement code (conceptual-data-model.md §4.13, §9).
/// </summary>
public sealed class SqliteDatabaseSeeder
{
    private readonly ILocalClock _clock;

    public SqliteDatabaseSeeder(ILocalClock clock)
    {
        _clock = clock;
    }

    public async Task<SeedResult> SeedAsync(
        BoundlessDbContext context,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var profile = await context.LocalProfiles
            .OrderBy(p => p.CreatedAtUtc)
            .ThenBy(p => p.Id)
            .FirstOrDefaultAsync(cancellationToken);

        var profileCreated = false;
        if (profile is null)
        {
            profile = LocalProfile.Create(_clock.UtcNow);
            await context.LocalProfiles.AddAsync(profile, cancellationToken);
            profileCreated = true;
        }

        var focusProfilesCreated = 0;
        foreach (var focusProfile in BuiltInProfiles.Seed())
        {
            var exists = await context.FocusProfiles
                .AnyAsync(p => p.Code == focusProfile.Code, cancellationToken);
            if (exists)
            {
                continue;
            }

            await context.FocusProfiles.AddAsync(focusProfile, cancellationToken);
            focusProfilesCreated++;
        }

        var achievementsCreated = 0;
        foreach (var definition in ApprovedAchievements())
        {
            var exists = await context.AchievementDefinitions
                .AnyAsync(d => d.Code == definition.Code, cancellationToken);
            if (exists)
            {
                continue;
            }

            await context.AchievementDefinitions.AddAsync(definition, cancellationToken);
            achievementsCreated++;
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new SeedResult(profile.Id, profileCreated, focusProfilesCreated, achievementsCreated);
    }

    /// <summary>
    /// The approved MVP achievement set (domain-business-rules.md §8.9). Thresholds
    /// are in the criteria's own unit: sessions, active minutes, streak days, Strict
    /// sessions, and tasks.
    /// </summary>
    public static IReadOnlyList<AchievementDefinition> ApprovedAchievements() =>
    [
        AchievementDefinition.Create(
            "ACH-001",
            "First Focus",
            "Complete the first qualified focus session.",
            AchievementCriteriaType.SessionCount,
            threshold: 1,
            xpAward: 50,
            economyRuleVersion: EconomyRules.Version),
        AchievementDefinition.Create(
            "ACH-002",
            "One Hour",
            "Accumulate 60 verified active-study minutes.",
            AchievementCriteriaType.ActiveMinutes,
            threshold: 60,
            xpAward: 75,
            economyRuleVersion: EconomyRules.Version),
        AchievementDefinition.Create(
            "ACH-003",
            "Three-Day Rhythm",
            "Reach a 3-day streak.",
            AchievementCriteriaType.StreakDays,
            threshold: 3,
            xpAward: 100,
            economyRuleVersion: EconomyRules.Version),
        AchievementDefinition.Create(
            "ACH-004",
            "Deep Work",
            "Complete 5 Strict focus sessions with at least 10 active minutes.",
            AchievementCriteriaType.StrictSessionCount,
            threshold: 5,
            xpAward: 100,
            economyRuleVersion: EconomyRules.Version),
        AchievementDefinition.Create(
            "ACH-005",
            "Task Finisher",
            "Complete 10 tasks.",
            AchievementCriteriaType.TaskCount,
            threshold: 10,
            xpAward: 75,
            economyRuleVersion: EconomyRules.Version),
    ];
}
