using BoundlessStudy.Application.Abstractions;

namespace BoundlessStudy.Infrastructure.Persistence;

public sealed record SqliteBootstrapResult(
    MigrationOutcome Migration,
    SchemaHandshakeResult Handshake,
    SeedResult Seed,
    string? BackupRecordWarning);

/// <summary>
/// The agent's database startup sequence: directories, migrations with a verified
/// pre-migration backup, schema handshake, idempotent seed, and backup-record
/// bookkeeping. Nothing in this class is called by the UI, service, bridge, or
/// extension; they never open the database (ADR-0005).
/// </summary>
public sealed class SqliteDatabaseBootstrapper
{
    private readonly SqliteDatabaseOptions _options;
    private readonly ILocalClock _clock;
    private readonly DatabaseBackupService _backups;

    public SqliteDatabaseBootstrapper(SqliteDatabaseOptions options, ILocalClock clock)
    {
        _options = options;
        _clock = clock;
        _backups = new DatabaseBackupService(options, clock);
    }

    public async Task<SqliteBootstrapResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var databaseDirectory = Path.GetDirectoryName(_options.DatabasePath);
        if (!string.IsNullOrEmpty(databaseDirectory))
        {
            DataDirectorySecurity.CreateWithOwnerOnlyAcl(databaseDirectory);
        }

        DataDirectorySecurity.CreateWithOwnerOnlyAcl(_options.ResolvedBackupDirectory);

        var runner = new SqliteMigrationRunner(_options, _backups);
        var migration = await runner.RunAsync(cancellationToken);

        await using var context = new BoundlessDbContext(_options.BuildDbContextOptions());
        var handshake = await SchemaHandshake.VerifyAsync(context, cancellationToken);

        var seeder = new SqliteDatabaseSeeder(_clock);
        var seed = await seeder.SeedAsync(context, cancellationToken);

        string? warning = null;
        if (migration.PreMigrationBackup is not null)
        {
            try
            {
                await _backups.RecordAsync(
                    context,
                    seed.LocalProfileId,
                    migration.PreMigrationBackup,
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The verified backup file is authoritative; a metadata insert must
                // never block startup or hide the backup (conceptual-data-model.md §4.16).
                warning = $"The pre-migration backup exists at '{migration.PreMigrationBackup.FilePath}', " +
                    $"but its BackupRecord row could not be written: {ex.Message}";
            }
        }

        try
        {
            // Retention prunes files outside the transaction that created them.
            // Reconcile metadata on every startup so a restore candidate never
            // points at a file that no longer exists.
            await _backups.ReconcileRecordsAsync(context, seed.LocalProfileId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            warning ??= $"Backup metadata could not be reconciled with the backup folder: {ex.Message}";
        }

        return new SqliteBootstrapResult(migration, handshake, seed, warning);
    }
}
