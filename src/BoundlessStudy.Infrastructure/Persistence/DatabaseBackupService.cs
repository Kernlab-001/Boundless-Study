using System.Security.Cryptography;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>Metadata for one completed, integrity-checked backup file.</summary>
public sealed record BackupArtifact(
    string FilePath,
    string FileName,
    BackupReason Reason,
    string SchemaVersion,
    string AppVersion,
    string Sha256,
    long SizeBytes,
    DateTime CreatedAtUtc);

/// <summary>
/// Consistent online SQLite snapshots (ADR-0005, conceptual-data-model.md §8-§9).
/// A backup is created through <see cref="SqliteConnection.BackupDatabase"/>, never
/// by copying an open database file, and is verified with <c>integrity_check</c>
/// before it is reported as usable.
/// </summary>
public sealed class DatabaseBackupService
{
    private readonly SqliteDatabaseOptions _options;
    private readonly ILocalClock _clock;

    public DatabaseBackupService(SqliteDatabaseOptions options, ILocalClock clock)
    {
        _options = options;
        _clock = clock;

        if (options.AutomaticBackupRetentionCount < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AutomaticBackupRetentionCount,
                "Automatic backup retention must keep at least one verified backup.");
        }
    }

    public async Task<BackupArtifact> CreateAsync(
        BackupReason reason,
        string schemaVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaVersion);

        if (!File.Exists(_options.DatabasePath))
        {
            throw new FileNotFoundException("There is no database to back up.", _options.DatabasePath);
        }

        DataDirectorySecurity.CreateWithOwnerOnlyAcl(_options.ResolvedBackupDirectory);

        var createdAtUtc = _clock.UtcNow;
        var fileName = BuildFileName(reason, createdAtUtc);
        var filePath = Path.Combine(_options.ResolvedBackupDirectory, fileName);

        var sourceConnectionString = _options.BuildConnectionString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString();

        await using (var source = new SqliteConnection(sourceConnectionString))
        await using (var destination = new SqliteConnection(destinationConnectionString))
        {
            await source.OpenAsync(cancellationToken);
            await destination.OpenAsync(cancellationToken);
            source.BackupDatabase(destination);
        }

        await VerifyIntegrityAsync(filePath, cancellationToken);

        var sha256 = await ComputeSha256Async(filePath, cancellationToken);
        var sizeBytes = new FileInfo(filePath).Length;

        var artifact = new BackupArtifact(
            filePath,
            fileName,
            reason,
            schemaVersion,
            AppVersion(),
            sha256,
            sizeBytes,
            createdAtUtc);

        PruneRetainedBackups(filePath);

        return artifact;
    }

    /// <summary>
    /// Adds the BackupRecord row after the schema is known to contain the table.
    /// The file itself is authoritative if this row cannot be written.
    /// </summary>
    public async Task RecordAsync(
        BoundlessDbContext context,
        Guid localProfileId,
        BackupArtifact artifact,
        CancellationToken cancellationToken = default)
    {
        var record = BackupRecord.Create(
            localProfileId,
            artifact.FileName,
            artifact.Reason,
            artifact.SchemaVersion,
            artifact.AppVersion,
            artifact.Sha256,
            artifact.SizeBytes,
            artifact.CreatedAtUtc);
        record.MarkVerified(artifact.CreatedAtUtc);

        await context.BackupRecords.AddAsync(record, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // A pruned file must not leave a restore-candidate row behind. The file is
        // authoritative, so records whose file no longer exists are removed.
        await ReconcileRecordsAsync(context, localProfileId, cancellationToken);
    }

    /// <summary>
    /// Removes BackupRecord rows whose files no longer exist. Retention prunes only
    /// Automatic and PreMigration files; a record for a missing file would otherwise
    /// advertise an unrestorable backup (conceptual-data-model.md §4.16).
    /// </summary>
    public async Task<int> ReconcileRecordsAsync(
        BoundlessDbContext context,
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        var records = await context.BackupRecords
            .Where(r => r.LocalProfileId == localProfileId)
            .ToListAsync(cancellationToken);

        var missing = records
            .Where(r => !File.Exists(Path.Combine(_options.ResolvedBackupDirectory, r.FileName)))
            .ToArray();

        if (missing.Length == 0)
        {
            return 0;
        }

        context.BackupRecords.RemoveRange(missing);
        await context.SaveChangesAsync(cancellationToken);
        return missing.Length;
    }

    private static async Task VerifyIntegrityAsync(string filePath, CancellationToken cancellationToken)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = (string?)await command.ExecuteScalarAsync(cancellationToken);

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"The backup at '{filePath}' failed integrity_check: {result ?? "(no result)"}.");
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    private string BuildFileName(BackupReason reason, DateTime createdAtUtc) =>
        reason switch
        {
            BackupReason.PreMigration =>
                $"{PersistenceConstants.PreMigrationBackupFilePrefix}-{createdAtUtc:yyyyMMddTHHmmssfffZ}-{NewSuffix()}.db",
            BackupReason.Automatic =>
                $"{PersistenceConstants.AutomaticBackupFilePrefix}-{createdAtUtc:yyyyMMddTHHmmssfffZ}-{NewSuffix()}.db",
            BackupReason.Manual =>
                $"{PersistenceConstants.ManualBackupFilePrefix}-{createdAtUtc:yyyyMMddTHHmmssfffZ}-{NewSuffix()}.db",
            BackupReason.PreRestore =>
                $"{PersistenceConstants.PreRestoreBackupFilePrefix}-{createdAtUtc:yyyyMMddTHHmmssfffZ}-{NewSuffix()}.db",
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unknown backup reason."),
        };

    private static string NewSuffix() => Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// §9: keep the seven most recent automatic backups plus the most recent
    /// pre-migration backup. Manual and pre-restore backups are user-controlled and
    /// are never pruned automatically. The backup that was just created is always
    /// preserved, even if the system clock moved backwards.
    /// </summary>
    private void PruneRetainedBackups(string preservedPath)
    {
        if (!Directory.Exists(_options.ResolvedBackupDirectory))
        {
            return;
        }

        Prune(
            PersistenceConstants.AutomaticBackupFilePrefix,
            _options.AutomaticBackupRetentionCount,
            preservedPath);

        Prune(PersistenceConstants.PreMigrationBackupFilePrefix, 1, preservedPath);
    }

    private void Prune(string prefix, int keep, string preservedPath)
    {
        var files = Directory
            .EnumerateFiles(_options.ResolvedBackupDirectory, $"{prefix}-*.db")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.Name, StringComparer.Ordinal)
            .ToArray();

        var preserved = files.FirstOrDefault(file =>
            string.Equals(file.FullName, preservedPath, StringComparison.OrdinalIgnoreCase));
        var keepOthers = Math.Max(0, keep - (preserved is null ? 0 : 1));

        var stale = files
            .Where(file => !string.Equals(file.FullName, preservedPath, StringComparison.OrdinalIgnoreCase))
            .Skip(keepOthers)
            .ToArray();

        foreach (var file in stale)
        {
            try
            {
                file.Delete();
            }
            catch (IOException)
            {
                // A backup that cannot be pruned is retained; retention must never
                // fail the operation that created a new verified backup.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string AppVersion() =>
        typeof(DatabaseBackupService).Assembly.GetName().Version?.ToString() ?? "0.0.0";
}
