using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BoundlessStudy.Infrastructure.Persistence.Configurations;

/// <summary>RewardLedgerEntry (§4.12); append-only, unique idempotency key.</summary>
public sealed class RewardLedgerEntryConfiguration : IEntityTypeConfiguration<RewardLedgerEntry>
{
    private static readonly ValueConverter<CurrencyCode, string> CurrencyConverter = new(
        currency => CurrencyToDatabase(currency),
        currency => CurrencyFromDatabase(currency));

    public void Configure(EntityTypeBuilder<RewardLedgerEntry> builder)
    {
        builder.ToTable("RewardLedgerEntry", table =>
        {
            table.HasCheckConstraint("CK_RewardLedgerEntry_Currency", "\"CurrencyCode\" IN ('XP', 'TOKEN')");
            table.HasCheckConstraint("CK_RewardLedgerEntry_Amount", "\"Amount\" > 0");
            table.HasCheckConstraint("CK_RewardLedgerEntry_SourceType",
                "\"SourceType\" IN ('SessionCompletion', 'SessionPartial', 'TaskCompletion', 'AchievementUnlock', 'Adjustment')");
            table.HasCheckConstraint("CK_RewardLedgerEntry_SourceId", "length(trim(\"SourceId\")) BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_RewardLedgerEntry_IdempotencyKey", "length(trim(\"IdempotencyKey\")) BETWEEN 1 AND 200");
            table.HasCheckConstraint("CK_RewardLedgerEntry_Notes", "\"Notes\" IS NULL OR length(\"Notes\") <= 500");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnType("TEXT");
        builder.Property(e => e.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.CurrencyCode).HasConversion(CurrencyConverter).HasMaxLength(10).IsRequired();
        builder.Property(e => e.Amount).IsRequired();
        builder.Property(e => e.SourceType).IsStringEnum(32).IsRequired();
        builder.Property(e => e.SourceId).HasMaxLength(100).IsRequired();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(200).IsRequired();
        builder.Property(e => e.AwardedAtUtc).IsUtc().IsRequired();
        builder.Property(e => e.AwardLocalDate).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.UtcOffsetMinutes).IsRequired();
        builder.Property(e => e.EconomyRuleVersion).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(500);
        builder.Property(e => e.CreatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(e => e.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.IdempotencyKey).IsUnique();
        builder.HasIndex(e => new { e.LocalProfileId, e.AwardLocalDate, e.CurrencyCode });
        builder.HasIndex(e => new { e.LocalProfileId, e.AwardedAtUtc });
    }

    private static string CurrencyToDatabase(CurrencyCode currency) => currency switch
    {
        CurrencyCode.Xp => "XP",
        CurrencyCode.Token => "TOKEN",
        _ => throw new InvalidOperationException($"Unknown currency code {currency}."),
    };

    private static CurrencyCode CurrencyFromDatabase(string currency) => currency switch
    {
        "XP" => CurrencyCode.Xp,
        "TOKEN" => CurrencyCode.Token,
        _ => throw new InvalidOperationException($"Unknown stored currency code '{currency}'."),
    };
}

/// <summary>AchievementDefinition (§4.13); Code is permanent identity.</summary>
public sealed class AchievementDefinitionConfiguration : IEntityTypeConfiguration<AchievementDefinition>
{
    public void Configure(EntityTypeBuilder<AchievementDefinition> builder)
    {
        builder.ToTable("AchievementDefinition", table =>
        {
            table.HasCheckConstraint("CK_AchievementDefinition_Code", "length(trim(\"Code\")) BETWEEN 1 AND 40");
            table.HasCheckConstraint("CK_AchievementDefinition_Name", "length(trim(\"Name\")) BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_AchievementDefinition_Description", "length(\"Description\") <= 500");
            table.HasCheckConstraint("CK_AchievementDefinition_CriteriaType",
                "\"CriteriaType\" IN ('SessionCount', 'ActiveMinutes', 'StreakDays', 'StrictSessionCount', 'TaskCount')");
            table.HasCheckConstraint("CK_AchievementDefinition_Threshold", "\"Threshold\" > 0");
            table.HasCheckConstraint("CK_AchievementDefinition_XpAward", "\"XpAward\" > 0");
        });

        builder.HasKey(d => d.Code);
        builder.Property(d => d.Code).HasMaxLength(AchievementDefinition.MaxCodeLength).IsRequired();
        builder.Property(d => d.Name).HasMaxLength(AchievementDefinition.MaxNameLength).IsRequired();
        builder.Property(d => d.Description).HasMaxLength(AchievementDefinition.MaxDescriptionLength).IsRequired();
        builder.Property(d => d.CriteriaType).IsStringEnum(32).IsRequired();
        builder.Property(d => d.Threshold).IsRequired();
        builder.Property(d => d.XpAward).IsRequired();
        builder.Property(d => d.EconomyRuleVersion).HasMaxLength(32).IsRequired();
        builder.Property(d => d.IsEnabled).IsRequired();
    }
}

/// <summary>UserAchievement (§4.14); unique (profile, achievement code).</summary>
public sealed class UserAchievementConfiguration : IEntityTypeConfiguration<UserAchievement>
{
    public void Configure(EntityTypeBuilder<UserAchievement> builder)
    {
        builder.ToTable("UserAchievement", table =>
        {
            table.HasCheckConstraint("CK_UserAchievement_Code", "length(trim(\"AchievementCode\")) BETWEEN 1 AND 40");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnType("TEXT");
        builder.Property(a => a.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(a => a.AchievementCode).HasMaxLength(AchievementDefinition.MaxCodeLength).IsRequired();
        builder.Property(a => a.UnlockedAtUtc).IsUtc().IsRequired();
        builder.Property(a => a.LedgerEntryId).HasColumnType("TEXT");
        builder.Property(a => a.SeenAtUtc).IsUtc();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(a => a.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<AchievementDefinition>()
            .WithMany()
            .HasForeignKey(a => a.AchievementCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RewardLedgerEntry>()
            .WithMany()
            .HasForeignKey(a => a.LedgerEntryId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(a => new { a.LocalProfileId, a.AchievementCode }).IsUnique();
        builder.HasIndex(a => a.LedgerEntryId);
    }
}
