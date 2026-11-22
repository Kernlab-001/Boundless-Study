using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoundlessStudy.Infrastructure.Persistence.Configurations;

/// <summary>AppSetting (§4.15); value is validated JSON and never a secret.</summary>
public sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("AppSetting", table =>
        {
            table.HasCheckConstraint("CK_AppSetting_Key", "length(trim(\"Key\")) BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_AppSetting_ValueJson", "json_valid(\"ValueJson\")");
        });

        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(AppSetting.MaxKeyLength).IsRequired();
        builder.Property(s => s.ValueJson).IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsUtc().IsRequired();
        builder.HasIndex(s => s.UpdatedAtUtc);
    }
}

/// <summary>BackupRecord (§4.16); reconstructable by scanning the backup directory.</summary>
public sealed class BackupRecordConfiguration : IEntityTypeConfiguration<BackupRecord>
{
    public void Configure(EntityTypeBuilder<BackupRecord> builder)
    {
        builder.ToTable("BackupRecord", table =>
        {
            table.HasCheckConstraint("CK_BackupRecord_FileName", "length(trim(\"FileName\")) BETWEEN 1 AND 260");
            table.HasCheckConstraint("CK_BackupRecord_Reason", "\"Reason\" IN ('Manual', 'Automatic', 'PreMigration', 'PreRestore')");
            table.HasCheckConstraint("CK_BackupRecord_Sha256", "length(\"Sha256\") = 64");
            table.HasCheckConstraint("CK_BackupRecord_SizeBytes", "\"SizeBytes\" >= 0");
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnType("TEXT");
        builder.Property(r => r.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(r => r.FileName).HasMaxLength(BackupRecord.MaxFileNameLength).IsRequired();
        builder.Property(r => r.Reason).IsStringEnum(16).IsRequired();
        builder.Property(r => r.SchemaVersion).HasMaxLength(200).IsRequired();
        builder.Property(r => r.AppVersion).HasMaxLength(50).IsRequired();
        builder.Property(r => r.Sha256).HasMaxLength(64).IsRequired();
        builder.Property(r => r.SizeBytes).IsRequired();
        builder.Property(r => r.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(r => r.VerifiedAtUtc).IsUtc();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(r => r.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.LocalProfileId, r.CreatedAtUtc });
    }
}
