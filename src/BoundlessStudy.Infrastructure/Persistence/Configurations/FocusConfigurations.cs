using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoundlessStudy.Infrastructure.Persistence.Configurations;

/// <summary>FocusSession (§4.5) including the one-active-session-per-profile rule.</summary>
public sealed class FocusSessionConfiguration : IEntityTypeConfiguration<FocusSession>
{
    private const string ActiveStatesSql = "'Active', 'Blocked', 'SystemPaused', 'RecoverableInterrupted'";

    public void Configure(EntityTypeBuilder<FocusSession> builder)
    {
        builder.ToTable("FocusSession", table =>
        {
            table.HasCheckConstraint("CK_FocusSession_PlannedFocusSeconds",
                "\"PlannedFocusSeconds\" BETWEEN 300 AND 10800 AND \"PlannedFocusSeconds\" % 300 = 0");
            table.HasCheckConstraint("CK_FocusSession_State",
                "\"State\" IN ('Active', 'Blocked', 'SystemPaused', 'Completed', 'EndedEarly', 'RecoverableInterrupted', 'Abandoned')");
            table.HasCheckConstraint("CK_FocusSession_Counters",
                "\"ElapsedSeconds\" >= 0 AND \"ActiveStudySeconds\" >= 0 AND \"IdleSeconds\" >= 0 AND \"ProtectionGapSeconds\" >= 0 AND \"ViolationCount\" >= 0 AND \"ExceptionCount\" >= 0 AND \"RecoveryCount\" >= 0");
            table.HasCheckConstraint("CK_FocusSession_ActiveWithinElapsed", "\"ActiveStudySeconds\" <= \"ElapsedSeconds\"");
            table.HasCheckConstraint("CK_FocusSession_Completed",
                "\"State\" <> 'Completed' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" = 'Completed' AND \"ElapsedSeconds\" >= \"PlannedFocusSeconds\")");
            table.HasCheckConstraint("CK_FocusSession_EndedEarly",
                "\"State\" <> 'EndedEarly' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" IS NOT NULL AND \"EndReason\" <> 'Completed')");
            table.HasCheckConstraint("CK_FocusSession_Abandoned",
                "\"State\" <> 'Abandoned' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" IS NOT NULL AND \"EndReason\" <> 'Completed')");
            table.HasCheckConstraint("CK_FocusSession_ProtectionStatus",
                "\"ProtectionStatus\" IN ('NotRequired', 'NotConfigured', 'Protected', 'Degraded', 'Unavailable', 'Unknown')");
            table.HasCheckConstraint("CK_FocusSession_VerificationStatus",
                "\"VerificationStatus\" IN ('Verified', 'Degraded', 'Unverified')");
            table.HasCheckConstraint("CK_FocusSession_EndReason",
                "\"EndReason\" IS NULL OR \"EndReason\" IN ('Completed', 'UserEnded', 'AgentCrash', 'Shutdown', 'PowerLoss', 'OverlayFailure', 'EnforcementFailure', 'Unknown')");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnType("TEXT");
        builder.Property(s => s.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(s => s.TaskId).HasColumnType("TEXT");
        builder.Property(s => s.SubjectId).HasColumnType("TEXT");
        builder.Property(s => s.TaskTitleSnapshot).HasMaxLength(200);
        builder.Property(s => s.SubjectNameSnapshot).HasMaxLength(100);
        builder.Property(s => s.ProfileCode).HasMaxLength(FocusProfile.MaxCodeLength).IsRequired();
        builder.Property(s => s.PlannedFocusSeconds).IsRequired();
        builder.Property(s => s.State).IsStringEnum(32).IsRequired();
        builder.Property(s => s.StartedAtUtc).IsUtc().IsRequired();
        builder.Property(s => s.EndedAtUtc).IsUtc();
        builder.Property(s => s.LastCheckpointAtUtc).IsUtc();
        builder.Property(s => s.InterruptedAtUtc).IsUtc();
        builder.Property(s => s.ElapsedSeconds).IsRequired();
        builder.Property(s => s.ActiveStudySeconds).IsRequired();
        builder.Property(s => s.IdleSeconds).IsRequired();
        builder.Property(s => s.ProtectionGapSeconds).IsRequired();
        builder.Property(s => s.ViolationCount).IsRequired();
        builder.Property(s => s.ExceptionCount).IsRequired();
        builder.Property(s => s.RecoveryCount).IsRequired();
        builder.Property(s => s.ProtectionStatus).IsStringEnum(32).IsRequired();
        builder.Property(s => s.VerificationStatus).IsStringEnum(16).IsRequired();
        builder.Property(s => s.EndReason).IsStringEnum(32);
        builder.Property(s => s.AppRuleVersion).HasMaxLength(100).IsRequired();
        builder.Property(s => s.EconomyRuleVersion).HasMaxLength(32).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsUtc().IsRequired();

        // Derived from LastCheckpointAtUtc + ElapsedSeconds; no duplicate column (M2 review A5).
        builder.Ignore(s => s.LastCheckpointElapsedSeconds);

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(s => s.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<StudyTask>()
            .WithMany()
            .HasForeignKey(s => s.TaskId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<Subject>()
            .WithMany()
            .HasForeignKey(s => s.SubjectId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne<FocusProfile>()
            .WithMany()
            .HasForeignKey(s => s.ProfileCode)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => new { s.LocalProfileId, s.State });
        builder.HasIndex(s => new { s.LocalProfileId, s.EndedAtUtc });
        builder.HasIndex(s => s.TaskId);
        builder.HasIndex(s => s.LocalProfileId)
            .HasDatabaseName("UX_FocusSession_OneActivePerProfile")
            .IsUnique()
            .HasFilter($"\"State\" IN ({ActiveStatesSql})");
    }
}

/// <summary>BreakSession (§4.6) including the one-active-break rule.</summary>
public sealed class BreakSessionConfiguration : IEntityTypeConfiguration<BreakSession>
{
    public void Configure(EntityTypeBuilder<BreakSession> builder)
    {
        builder.ToTable("BreakSession", table =>
        {
            table.HasCheckConstraint("CK_BreakSession_PlannedSeconds", "\"PlannedSeconds\" BETWEEN 60 AND 3600");
            table.HasCheckConstraint("CK_BreakSession_State", "\"State\" IN ('Active', 'Completed', 'EndedEarly', 'Interrupted')");
            table.HasCheckConstraint("CK_BreakSession_ElapsedSeconds", "\"ElapsedSeconds\" >= 0");
            table.HasCheckConstraint("CK_BreakSession_Completed",
                "\"State\" <> 'Completed' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" = 'Completed')");
            table.HasCheckConstraint("CK_BreakSession_EndedEarly",
                "\"State\" <> 'EndedEarly' OR (\"EndedAtUtc\" IS NOT NULL AND \"EndReason\" IS NOT NULL AND \"EndReason\" <> 'Completed')");
            table.HasCheckConstraint("CK_BreakSession_EndReason",
                "\"EndReason\" IS NULL OR \"EndReason\" IN ('Completed', 'UserEnded', 'LockOrSleepTimeout', 'Shutdown', 'Unknown')");
        });

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnType("TEXT");
        builder.Property(b => b.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(b => b.FocusSessionId).HasColumnType("TEXT");
        builder.Property(b => b.PlannedSeconds).IsRequired();
        builder.Property(b => b.State).IsStringEnum(16).IsRequired();
        builder.Property(b => b.StartedAtUtc).IsUtc().IsRequired();
        builder.Property(b => b.EndedAtUtc).IsUtc();
        builder.Property(b => b.ElapsedSeconds).IsRequired();
        builder.Property(b => b.EndReason).IsStringEnum(32);
        builder.Property(b => b.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(b => b.UpdatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(b => b.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<FocusSession>()
            .WithMany()
            .HasForeignKey(b => b.FocusSessionId)
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasIndex(b => new { b.LocalProfileId, b.State });
        builder.HasIndex(b => b.FocusSessionId);
        builder.HasIndex(b => b.LocalProfileId)
            .HasDatabaseName("UX_BreakSession_OneActivePerProfile")
            .IsUnique()
            .HasFilter("\"State\" = 'Active'");
    }
}

/// <summary>SessionEvent (§4.7); append-only and unique per session sequence.</summary>
public sealed class SessionEventConfiguration : IEntityTypeConfiguration<SessionEvent>
{
    public void Configure(EntityTypeBuilder<SessionEvent> builder)
    {
        builder.ToTable("SessionEvent", table =>
        {
            table.HasCheckConstraint("CK_SessionEvent_Sequence", "\"Sequence\" >= 1");
            table.HasCheckConstraint("CK_SessionEvent_SessionType", "\"SessionType\" IN ('Focus', 'Break')");
            table.HasCheckConstraint("CK_SessionEvent_EventType",
                "\"EventType\" IN ('Start', 'Blocked', 'Unblocked', 'IdleStarted', 'IdleEnded', 'SystemPaused', 'SystemResumed', 'BreakStarted', 'BreakEnded', 'ExceptionGranted', 'ProtectionDegraded', 'ProtectionRestored', 'Recovered', 'Completed', 'EndedEarly', 'Interrupted', 'Abandoned', 'EnforcementFailed', 'RewardCreated', 'AchievementUnlocked')");
            table.HasCheckConstraint("CK_SessionEvent_PayloadJson", "length(\"PayloadJson\") <= 2000");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnType("TEXT");
        builder.Property(e => e.SessionType).IsStringEnum(8).IsRequired();
        builder.Property(e => e.SessionId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.Sequence).IsRequired();
        builder.Property(e => e.EventType).IsStringEnum(32).IsRequired();
        builder.Property(e => e.OccurredAtUtc).IsUtc().IsRequired();
        builder.Property(e => e.LocalDateAtEvent).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.UtcOffsetMinutes).IsRequired();
        builder.Property(e => e.PayloadJson).HasMaxLength(SessionEvent.MaxPayloadJsonLength).IsRequired();
        builder.Property(e => e.CreatedAtUtc).IsUtc().IsRequired();

        // §4.7 / §6: unique per session. SessionId is a GUID and is globally unique;
        // SessionType is retained in the lookup index for fast typed queries.
        builder.HasIndex(e => new { e.SessionId, e.Sequence }).IsUnique();
        builder.HasIndex(e => new { e.SessionType, e.SessionId });
    }
}

/// <summary>SessionException (§4.8); expiry never removes the underlying rule.</summary>
public sealed class SessionExceptionConfiguration : IEntityTypeConfiguration<SessionException>
{
    public void Configure(EntityTypeBuilder<SessionException> builder)
    {
        builder.ToTable("SessionException", table =>
        {
            table.HasCheckConstraint("CK_SessionException_TargetType", "\"TargetType\" IN ('Application', 'Website')");
            table.HasCheckConstraint("CK_SessionException_TargetId", "length(trim(\"TargetId\")) BETWEEN 1 AND 253");
            table.HasCheckConstraint("CK_SessionException_Expiry", "\"ExpiresAtUtc\" > \"GrantedAtUtc\"");
            table.HasCheckConstraint("CK_SessionException_ReasonCategory",
                "\"ReasonCategory\" IN ('Technical', 'Accessibility', 'StudyResource', 'Emergency', 'Other')");
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).HasColumnType("TEXT");
        builder.Property(e => e.FocusSessionId).HasColumnType("TEXT").IsRequired();
        builder.Property(e => e.TargetType).IsStringEnum(16).IsRequired();
        builder.Property(e => e.TargetId).HasMaxLength(253).IsRequired();
        builder.Property(e => e.GrantedAtUtc).IsUtc().IsRequired();
        builder.Property(e => e.ExpiresAtUtc).IsUtc().IsRequired();
        builder.Property(e => e.ReasonCategory).IsStringEnum(32).IsRequired();
        builder.Property(e => e.WasAutoExpired).IsRequired();
        builder.Property(e => e.CreatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<FocusSession>()
            .WithMany()
            .HasForeignKey(e => e.FocusSessionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.FocusSessionId);
    }
}
