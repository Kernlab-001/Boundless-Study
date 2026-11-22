using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BoundlessStudy.Infrastructure.Persistence.Configurations;

/// <summary>BlockedApp (§4.9).</summary>
public sealed class BlockedAppConfiguration : IEntityTypeConfiguration<BlockedApp>
{
    public void Configure(EntityTypeBuilder<BlockedApp> builder)
    {
        builder.ToTable("BlockedApp", table =>
        {
            table.HasCheckConstraint("CK_BlockedApp_DisplayName", "length(trim(\"DisplayName\")) BETWEEN 1 AND 200");
            table.HasCheckConstraint("CK_BlockedApp_ExecutableName", "length(trim(\"NormalizedExecutableName\")) BETWEEN 1 AND 260");
            table.HasCheckConstraint("CK_BlockedApp_Path", "\"NormalizedExecutablePath\" IS NULL OR length(\"NormalizedExecutablePath\") <= 1000");
        });

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnType("TEXT");
        builder.Property(a => a.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(a => a.DisplayName).HasMaxLength(BlockedApp.MaxDisplayNameLength).IsRequired();
        builder.Property(a => a.NormalizedExecutablePath).HasMaxLength(1000);
        builder.Property(a => a.NormalizedExecutableName).HasMaxLength(260).IsRequired();
        builder.Property(a => a.IsEnabled).IsRequired();
        builder.Property(a => a.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(a => a.UpdatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(a => a.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(a => new { a.LocalProfileId, a.NormalizedExecutableName });
        builder.HasIndex(a => new { a.LocalProfileId, a.IsEnabled });
    }
}

/// <summary>BlockedSite (§4.10); normalized domain unique per profile.</summary>
public sealed class BlockedSiteConfiguration : IEntityTypeConfiguration<BlockedSite>
{
    public void Configure(EntityTypeBuilder<BlockedSite> builder)
    {
        builder.ToTable("BlockedSite", table =>
        {
            table.HasCheckConstraint("CK_BlockedSite_Domain", "length(trim(\"NormalizedDomain\")) BETWEEN 1 AND 253");
            table.HasCheckConstraint("CK_BlockedSite_DisplayName", "\"DisplayName\" IS NULL OR length(\"DisplayName\") <= 253");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnType("TEXT");
        builder.Property(s => s.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(s => s.NormalizedDomain).HasMaxLength(253).IsRequired();
        builder.Property(s => s.DisplayName).HasMaxLength(253);
        builder.Property(s => s.IsEnabled).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(s => s.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.LocalProfileId, s.NormalizedDomain }).IsUnique();
        builder.HasIndex(s => new { s.LocalProfileId, s.IsEnabled });
    }
}

/// <summary>AllowedSite (§4.11); allow-over-block precedence is evaluated by the UI/agent.</summary>
public sealed class AllowedSiteConfiguration : IEntityTypeConfiguration<AllowedSite>
{
    public void Configure(EntityTypeBuilder<AllowedSite> builder)
    {
        builder.ToTable("AllowedSite", table =>
        {
            table.HasCheckConstraint("CK_AllowedSite_Domain", "length(trim(\"NormalizedDomain\")) BETWEEN 1 AND 253");
            table.HasCheckConstraint("CK_AllowedSite_DisplayName", "\"DisplayName\" IS NULL OR length(\"DisplayName\") <= 253");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnType("TEXT");
        builder.Property(s => s.LocalProfileId).HasColumnType("TEXT").IsRequired();
        builder.Property(s => s.NormalizedDomain).HasMaxLength(253).IsRequired();
        builder.Property(s => s.DisplayName).HasMaxLength(253);
        builder.Property(s => s.IsEnabled).IsRequired();
        builder.Property(s => s.CreatedAtUtc).IsUtc().IsRequired();
        builder.Property(s => s.UpdatedAtUtc).IsUtc().IsRequired();

        builder.HasOne<LocalProfile>()
            .WithMany()
            .HasForeignKey(s => s.LocalProfileId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(s => new { s.LocalProfileId, s.NormalizedDomain }).IsUnique();
        builder.HasIndex(s => new { s.LocalProfileId, s.IsEnabled });
    }
}
