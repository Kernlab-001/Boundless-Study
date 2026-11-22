using System.Security.Cryptography;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class BackupAndRecoveryTests
{
    [Fact]
    public async Task Online_backup_is_integrity_checked_hashed_and_recordable()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var service = new DatabaseBackupService(db.Options, db.Clock);
        var artifact = await service.CreateAsync(BackupReason.Manual, "InitialCreate");

        Assert.True(File.Exists(artifact.FilePath));
        Assert.Equal(64, artifact.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", artifact.Sha256);
        Assert.True(artifact.SizeBytes > 0);
        Assert.StartsWith("boundless-backup-manual-", artifact.FileName, StringComparison.Ordinal);

        await using (var stream = File.OpenRead(artifact.FilePath))
        {
            var expected = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
            Assert.Equal(expected, artifact.Sha256);
        }

        await using (var backupConnection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = artifact.FilePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString()))
        {
            await backupConnection.OpenAsync();
            await using var command = backupConnection.CreateCommand();
            command.CommandText = "PRAGMA integrity_check;";
            Assert.Equal("ok", Convert.ToString(await command.ExecuteScalarAsync()));
        }

        await using var context = db.CreateContext();
        await service.RecordAsync(context, profileId, artifact);

        await using var repositories = db.Store.CreateRepositories();
        var records = await repositories.BackupRecords.ListAsync(profileId);
        var record = Assert.Single(records);
        Assert.Equal(artifact.FileName, record.FileName);
        Assert.Equal(artifact.Sha256, record.Sha256);
        Assert.Equal(artifact.SizeBytes, record.SizeBytes);
        Assert.Equal(BackupReason.Manual, record.Reason);
        Assert.NotNull(record.VerifiedAtUtc);
    }

    [Fact]
    public async Task Automatic_backups_keep_seven_and_pre_migration_backups_keep_the_newest_only()
    {
        await using var db = await TestDatabase.CreateAsync();
        var service = new DatabaseBackupService(db.Options, db.Clock);

        for (var i = 0; i < 10; i++)
        {
            await service.CreateAsync(BackupReason.Automatic, "InitialCreate");
            await Task.Delay(10);
        }

        for (var i = 0; i < 3; i++)
        {
            await service.CreateAsync(BackupReason.PreMigration, "InitialCreate");
            await Task.Delay(10);
        }

        for (var i = 0; i < 9; i++)
        {
            await service.CreateAsync(BackupReason.Manual, "InitialCreate");
            await Task.Delay(10);
        }

        Assert.Equal(7, Directory.GetFiles(db.Options.ResolvedBackupDirectory, "boundless-backup-auto-*.db").Length);
        Assert.Single(Directory.GetFiles(db.Options.ResolvedBackupDirectory, "boundless-pre-migration-*.db"));
        Assert.Equal(9, Directory.GetFiles(db.Options.ResolvedBackupDirectory, "boundless-backup-manual-*.db").Length);
    }

    [Fact]
    public async Task Migration_file_lock_timeout_is_bounded_and_does_not_touch_the_database()
    {
        await using var db = await TestDatabase.CreateAsync();
        var options = db.Options with { MigrationMutexWaitMilliseconds = 100 };
        var lockPath = options.DatabasePath + ".migration.lock";

        await using var held = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.None);

        var bootstrapper = new SqliteDatabaseBootstrapper(options, db.Clock);
        var error = await Assert.ThrowsAsync<MigrationLockTimeoutException>(
            () => bootstrapper.InitializeAsync());

        Assert.Contains("migration lock", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ok", await db.TextAsync("PRAGMA integrity_check;"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM LocalProfile;"));
    }

    [Fact]
    public async Task Pruning_a_pre_migration_file_removes_its_BackupRecord_row()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var service = new DatabaseBackupService(db.Options, db.Clock);
        await using var repositories = db.Store.CreateRepositories();

        var first = await service.CreateAsync(BackupReason.PreMigration, "InitialCreate");
        await service.RecordAsync(repositories.Context, profileId, first);

        db.Clock.UtcNow = db.Clock.UtcNow.AddSeconds(1);
        var second = await service.CreateAsync(BackupReason.PreMigration, "InitialCreate");
        await service.RecordAsync(repositories.Context, profileId, second);

        Assert.False(File.Exists(first.FilePath));
        Assert.True(File.Exists(second.FilePath));

        var record = Assert.Single(await repositories.BackupRecords.ListAsync(profileId));
        Assert.Equal(second.FileName, record.FileName);
    }

    [Fact]
    public void Automatic_backup_retention_must_keep_at_least_one_file()
    {
        var options = new SqliteDatabaseOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), "BoundlessStudy.Retention", "boundless.db"),
            AutomaticBackupRetentionCount = 0,
        };

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DatabaseBackupService(options, new TestLocalClock()));
    }

    [Fact]
    public async Task Failed_migration_keeps_the_original_database_readable_and_a_verified_backup()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindSingleAsync();
            Assert.NotNull(profile);
            await repositories.Subjects.AddAsync(Subject.Create(
                profile.Id, "Physics", null, db.Clock.UtcNow));
        }

        // Make the compiled InitialCreate migration look pending again while the
        // tables remain. The runner must back up first, then fail without losing data.
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "DELETE FROM \"__EFMigrationsHistory\"; " +
                "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") " +
                "VALUES (1, '2026-11-14T09:00:00.0000000Z');";
            await command.ExecuteNonQueryAsync();
        }

        await Assert.ThrowsAnyAsync<Exception>(() => db.Bootstrapper.InitializeAsync());

        var backups = Directory.GetFiles(db.Options.ResolvedBackupDirectory, "boundless-pre-migration-*.db");
        var backup = Assert.Single(backups);

        Assert.Equal("ok", await IntegrityAsync(backup));
        Assert.Equal(1, await CountProfilesAsync(backup));
        Assert.Equal(1, await CountSubjectsAsync(backup));

        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM LocalProfile;"));
        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM Subject;"));
        Assert.Equal("ok", await db.TextAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task Stale_migration_lock_is_cleared_but_a_live_writer_is_refused()
    {
        await using var db = await TestDatabase.CreateAsync();

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") " +
                "VALUES (1, '2026-11-14T09:00:00.0000000Z');";
            await command.ExecuteNonQueryAsync();
        }

        await using var writer = await db.OpenConnectionAsync();
        await using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            await begin.ExecuteNonQueryAsync();
        }

        try
        {
            var blocked = await MigrationLockRecovery.ClearIfStaleAsync(db.Options);
            Assert.True(blocked.LockRowFound);
            Assert.True(blocked.LiveWriterDetected);
            Assert.False(blocked.StaleLockCleared);
            Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsLock\";"));
        }
        finally
        {
            await using var rollback = writer.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            await rollback.ExecuteNonQueryAsync();
        }

        var recovered = await MigrationLockRecovery.ClearIfStaleAsync(db.Options);
        Assert.True(recovered.LockRowFound);
        Assert.False(recovered.LiveWriterDetected);
        Assert.True(recovered.StaleLockCleared);
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM \"__EFMigrationsLock\";"));
    }

    [Fact]
    public async Task Empty_migration_lock_table_needs_no_recovery()
    {
        await using var db = await TestDatabase.CreateAsync();

        var result = await MigrationLockRecovery.ClearIfStaleAsync(db.Options);

        Assert.True(result.LockTableExists);
        Assert.False(result.LockRowFound);
        Assert.False(result.StaleLockCleared);
        Assert.False(result.LiveWriterDetected);
    }

    private static async Task<string?> IntegrityAsync(string path)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountProfilesAsync(string path) =>
        await CountAsync(path, "SELECT count(*) FROM LocalProfile;");

    private static async Task<long> CountSubjectsAsync(string path) =>
        await CountAsync(path, "SELECT count(*) FROM Subject;");

    private static async Task<long> CountAsync(string path, string sql)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
