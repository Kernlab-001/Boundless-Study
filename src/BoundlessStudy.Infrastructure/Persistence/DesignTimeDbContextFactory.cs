using Microsoft.EntityFrameworkCore.Design;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Design-time only. Never points at the real per-user database, so running
/// <c>dotnet ef</c> cannot migrate or alter user data. Override the path with
/// <c>BOUNDLESSSTUDY_DESIGN_DB</c> when a disposable file is needed.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<BoundlessDbContext>
{
    public BoundlessDbContext CreateDbContext(string[] args)
    {
        var path = Environment.GetEnvironmentVariable("BOUNDLESSSTUDY_DESIGN_DB");
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Path.Combine(
                Path.GetTempPath(),
                "BoundlessStudy.DesignTime",
                PersistenceConstants.DatabaseFileName);
        }

        var options = new SqliteDatabaseOptions
        {
            DatabasePath = path,
            Pooling = false,
            CreateBackupBeforeMigration = false,
        };

        return new BoundlessDbContext(options.BuildDbContextOptions());
    }
}
