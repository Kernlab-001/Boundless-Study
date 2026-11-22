using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoundlessStudy.Infrastructure.Persistence.Configurations;

/// <summary>LocalProfile (conceptual-data-model.md §4.1).</summary>
public sealed class LocalProfileConfiguration : IEntityTypeConfiguration<LocalProfile>
{
    public void Configure(EntityTypeBuilder<LocalProfile> builder)
    {
        builder.ToTable("LocalProfile", table =>
        {
            table.HasCheckConstraint("CK_LocalProfile_DisplayName", "length(trim(\"DisplayName\")) BETWEEN 1 AND 80");
            table.HasCheckConstraint("CK_LocalProfile_CachedTotalXp", "\"CachedTotalXp\" >= 0");
            table.HasCheckConstraint("CK_LocalProfile_CachedLevel", "\"CachedLevel\" >= 1");
            table.HasCheckConstraint("CK_LocalProfile_InstallationMode", "\"InstallationMode\" IN ('Full', 'Limited')");
        });

        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnType("TEXT");
        builder.Property(p => p.DisplayName).HasMaxLength(LocalProfile.MaxDisplayNameLength).IsRequired();
        builder.Property(p => p.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(p => p.LastOpenedAtUtc).IsUtc();
        builder.Property(p => p.CachedTotalXp).IsRequired();
        builder.Property(p => p.CachedLevel).IsRequired();
        builder.Property(p => p.EconomyRuleVersion).HasMaxLength(32).IsRequired();
        builder.Property(p => p.IsOnboarded).IsRequired();
        builder.Property(p => p.InstallationMode).IsStringEnum(16).IsRequired();
        builder.Property(p => p.UpdatedAtUtc).IsUtc().IsRequired();
    }
}

/// <summary>Subject (conceptual-data-model.md §4.2). Archived subjects stay readable.</summary>
public sealed class SubjectConfiguration : IEntityTypeConfiguration<Subject>
{
    public void Configure(EntityTypeBuilder<Subject> builder)
    {
        builder.ToTable("Subject", table =>
        {
            table.HasCheckConstraint("CK_Subject_Name", "length(trim(\"Name\")) BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_Subject_Description", "length(\"Description\") <= 500");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnType("TEXT");
        builder.Property(s => s.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(s => s.Name).HasMaxLength(Subject.MaxNameLength).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(Subject.MaxDescriptionLength).IsRequired();
        builder.Property(s => s.IsArchived).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(s => s.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.LocalProfileId, s.IsArchived });
    }
}

/// <summary>StudyTask (conceptual-data-model.md §4.3); class name avoids Task ambiguity.</summary>
public sealed class StudyTaskConfiguration : IEntityTypeConfiguration<StudyTask>
{
    public void Configure(EntityTypeBuilder<StudyTask> builder)
    {
        builder.ToTable("StudyTask", table =>
        {
            table.HasCheckConstraint("CK_StudyTask_Title", "length(trim(\"Title\")) BETWEEN 1 AND 200");
            table.HasCheckConstraint("CK_StudyTask_Notes", "length(\"Notes\") <= 4000");
            table.HasCheckConstraint("CK_StudyTask_EstimatedMinutes",
                "\"EstimatedMinutes\" IS NULL OR (\"EstimatedMinutes\" BETWEEN 5 AND 600 AND \"EstimatedMinutes\" % 5 = 0)");
            table.HasCheckConstraint("CK_StudyTask_Priority", "\"Priority\" IN ('Low', 'Normal', 'High')");
            table.HasCheckConstraint("CK_StudyTask_Status", "\"Status\" IN ('Todo', 'InProgress', 'Done', 'Archived')");
            table.HasCheckConstraint("CK_StudyTask_CompletedAt", "\"Status\" <> 'Done' OR \"CompletedAtUtc\" IS NOT NULL");
            table.HasCheckConstraint("CK_StudyTask_SoftDelete", "\"IsDeleted\" = 0 OR \"DeletedAtUtc\" IS NOT NULL");
        });

        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnType("TEXT");
        builder.Property(t => t.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(t => t.SubjectId).HasColumnType("TEXT");
        builder.Property(t => t.Title).HasMaxLength(StudyTask.MaxTitleLength).IsRequired();
        builder.Property(t => t.Notes).HasMaxLength(StudyTask.MaxNotesLength).IsRequired();
        builder.Property(t => t.DueDateLocal).HasColumnType("TEXT");
        builder.Property(t => t.EstimatedMinutes);
        builder.Property(t => t.Priority).IsStringEnum(16).IsRequired();
        builder.Property(t => t.Status).IsStringEnum(16).IsRequired();
        builder.Property(t => t.PlannedForLocalDate).HasColumnType("TEXT");
        builder.Property(t => t.CompletedAtUtc).IsUtc();
        builder.Property(t => t.CompletionRewardId).HasColumnType("TEXT");
        builder.Property(t => t.IsDeleted).IsRequired();
        builder.Property(t => t.DeletedAtUtc).IsUtc();
        builder.Property(t => t.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(t => t.UpdatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(t => t.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Subject>()
            .WithMany()
            .HasForeignKey(t => t.SubjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<RewardLedgerEntry>()
            .WithMany()
            .HasForeignKey(t => t.CompletionRewardId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.LocalProfileId, t.Status, t.DueDateLocal });
        builder.HasIndex(t => new { t.LocalProfileId, t.PlannedForLocalDate });
        builder.HasIndex(t => t.SubjectId);
        builder.HasIndex(t => t.CompletionRewardId);
    }
}

/// <summary>Seeded reference data for the three approved focus profiles (§4.4).</summary>
public sealed class FocusProfileConfiguration : IEntityTypeConfiguration<FocusProfile>
{
    public void Configure(EntityTypeBuilder<FocusProfile> builder)
    {
        builder.ToTable("FocusProfile", table =>
        {
            table.HasCheckConstraint("CK_FocusProfile_Code", "length(trim(\"Code\")) BETWEEN 1 AND 20");
            table.HasCheckConstraint("CK_FocusProfile_DisplayName", "length(trim(\"DisplayName\")) BETWEEN 1 AND 40");
            table.HasCheckConstraint("CK_FocusProfile_EarlyExitMode",
                "\"EarlyExitMode\" IN ('Immediate', 'ConfirmationCooldown', 'TypedConfirmationCooldown')");
            table.HasCheckConstraint("CK_FocusProfile_Timings",
                "\"EarlyExitCooldownSeconds\" >= 0 AND \"MaxExceptionsPerSession\" >= 0 AND \"ExceptionDurationSeconds\" >= 0 AND \"ExceptionDelaySeconds\" >= 0");
            table.HasCheckConstraint("CK_FocusProfile_XpRate", "\"XpPerActiveMinute\" > 0");
        });

        builder.HasKey(p => p.Code);
        builder.Property(p => p.Code).HasMaxLength(FocusProfile.MaxCodeLength).IsRequired();
        builder.Property(p => p.DisplayName).HasMaxLength(FocusProfile.MaxDisplayNameLength).IsRequired();
        builder.Property(p => p.EarlyExitMode).IsStringEnum(32).IsRequired();
        builder.Property(p => p.EarlyExitCooldownSeconds).IsRequired();
        builder.Property(p => p.MaxExceptionsPerSession).IsRequired();
        builder.Property(p => p.ExceptionDurationSeconds).IsRequired();
        builder.Property(p => p.ExceptionDelaySeconds).IsRequired();
        builder.Property(p => p.XpPerActiveMinute).IsRequired();
        builder.Property(p => p.RequiresBrowserWhenSiteRulesExist).IsRequired();
        builder.Property(p => p.IsEnabled).IsRequired();
        builder.Property(p => p.EconomyRuleVersion).HasMaxLength(32).IsRequired();
    }
}
