using System.Security.Cryptography;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace BoundlessStudy.Infrastructure.Tests;

/// <summary>
/// Permanent upgrade coverage: a real database created by the first migration and
/// seeded before the second migration existed upgrades through the production
/// bootstrapper with a verified pre-migration backup and a BackupRecord row, and a
/// database from a newer lineage is rejected before anything is written.
/// </summary>
public sealed class MigrationUpgradeTests
{
    private const string InitialMigration = "20261114004133_InitialCreate";
    private const string SecondMigration = "20261115103904_AddAppSettingUpdatedAtIndex";
    private const string ThirdMigration = "20261120011431_AddFocusSessionInterruptedAtUtc";

    [Fact]
    public async Task Current_model_has_no_pending_migration_changes()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var context = db.CreateContext();

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task V1_database_upgrades_to_latest_with_verified_backup_record_and_preserved_data()
    {
        await using var db = await TestDatabase.CreateAsync(initialize: false);
        var markerCreatedAt = db.Clock.UtcNow;

        await CreateV1DatabaseAsync(db);
        await using (var seedContext = db.CreateContext())
        {
            var seed = await new SqliteDatabaseSeeder(db.Clock).SeedAsync(seedContext);
            seedContext.AppSettings.Add(AppSetting.Create(
                "upgrade.marker", "{\"kept\":true}", markerCreatedAt));
            await seedContext.SaveChangesAsync();
        }

        var bootstrap = await db.Store.InitializeAsync();
        var migration = bootstrap.Migration;

        Assert.False(migration.DatabaseCreated);
        Assert.Equal(new[] { SecondMigration, ThirdMigration }, migration.PendingBeforeMigration);
        Assert.Equal(3, migration.AppliedMigrations.Count);
        Assert.Equal(InitialMigration, migration.AppliedMigrations[0]);
        Assert.Equal(SecondMigration, migration.AppliedMigrations[1]);
        Assert.Equal(ThirdMigration, migration.AppliedMigrations[2]);
        Assert.Equal(ThirdMigration, migration.SchemaVersion);

        var backup = migration.PreMigrationBackup;
        Assert.NotNull(backup);
        Assert.Equal(BackupReason.PreMigration, backup.Reason);
        Assert.Equal(InitialMigration, backup.SchemaVersion);
        Assert.True(File.Exists(backup.FilePath));
        Assert.Equal("ok", await IntegrityCheckAsync(backup.FilePath));
        Assert.Equal(await Sha256Async(backup.FilePath), backup.Sha256);
        Assert.True(backup.SizeBytes > 0);
        Assert.NotNull(migration.LockRecovery);
        Assert.False(migration.LockRecovery.LiveWriterDetected);
        Assert.False(migration.LockRecovery.StaleLockCleared);
        Assert.Null(bootstrap.BackupRecordWarning);
        Assert.False(bootstrap.Seed.LocalProfileCreated);
        Assert.Equal(0, bootstrap.Seed.FocusProfilesCreated);
        Assert.Equal(0, bootstrap.Seed.AchievementDefinitionsCreated);

        var profileId = bootstrap.Seed.LocalProfileId;
        await using (var repositories = db.Store.CreateRepositories())
        {
            var records = await repositories.BackupRecords.ListAsync(profileId);
            var record = Assert.Single(records);
            Assert.Equal(backup.FileName, record.FileName);
            Assert.Equal(BackupReason.PreMigration, record.Reason);
            Assert.Equal(backup.Sha256, record.Sha256);
            Assert.Equal(backup.SizeBytes, record.SizeBytes);
            Assert.NotNull(record.VerifiedAtUtc);

            var marker = await repositories.AppSettings.FindAsync("upgrade.marker");
            Assert.NotNull(marker);
            Assert.Equal("{\"kept\":true}", marker.ValueJson);
            Assert.Equal(markerCreatedAt, marker.UpdatedAtUtc);
        }

        Assert.Equal(1, await db.ScalarAsync(
            "SELECT count(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_AppSetting_UpdatedAtUtc';"));
        Assert.Equal(3, await db.ScalarAsync(
            "SELECT count(*) FROM __EFMigrationsHistory WHERE MigrationId IN " +
            $"('{InitialMigration}', '{SecondMigration}', '{ThirdMigration}');"));

        // A second startup is idempotent: no migration, no new backup, no duplicate record.
        await using (var reopened = new SqliteDataStore(db.Options, db.Clock))
        {
            var second = await reopened.InitializeAsync();
            Assert.False(second.Migration.DatabaseCreated);
            Assert.Empty(second.Migration.PendingBeforeMigration);
            Assert.Null(second.Migration.PreMigrationBackup);
            Assert.Null(second.BackupRecordWarning);
            Assert.False(second.Seed.LocalProfileCreated);
        }

        await using var verification = db.Store.CreateRepositories();
        Assert.Single(await verification.BackupRecords.ListAsync(profileId));
        Assert.NotNull(await verification.AppSettings.FindAsync("upgrade.marker"));
        Assert.Equal(1, await verification.Context.LocalProfiles.CountAsync());
        Assert.Equal(3, await verification.Context.FocusProfiles.CountAsync());
        Assert.Equal(5, await verification.Context.AchievementDefinitions.CountAsync());
    }

    [Fact]
    public async Task V2_database_upgrades_to_v3_preserving_interrupted_abandoned_and_payload_history()
    {
        await using var db = await TestDatabase.CreateAsync(initialize: false);
        await CreateV2DatabaseAsync(db);

        Guid profileId;
        await using (var seedContext = db.CreateContext())
        {
            var seed = await new SqliteDatabaseSeeder(db.Clock).SeedAsync(seedContext);
            profileId = seed.LocalProfileId;
        }

        Assert.Equal(1, await db.ScalarAsync("SELECT count(*) FROM FocusProfile WHERE Code = 'Balanced';"));
        Assert.Equal(Stored(profileId), await db.TextAsync("SELECT Id FROM LocalProfile LIMIT 1;"));

        var pendingSessionId = Guid.NewGuid();
        var abandonedSessionId = Guid.NewGuid();
        var payloadAtLimit = new string('p', SessionEvent.MaxPayloadJsonLength);

        // SQLite stores Guid as uppercase TEXT; raw rows must use the same form or the
        // foreign keys and later EF reads will not match.
        static string Stored(Guid value) => value.ToString().ToUpperInvariant();

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO \"FocusSession\" (\"Id\", \"LocalProfileId\", \"ProfileCode\", \"PlannedFocusSeconds\", \"State\", " +
                "\"StartedAtUtc\", \"LastCheckpointAtUtc\", \"ElapsedSeconds\", \"ActiveStudySeconds\", \"IdleSeconds\", " +
                "\"ProtectionGapSeconds\", \"ViolationCount\", \"ExceptionCount\", \"RecoveryCount\", \"ProtectionStatus\", " +
                "\"VerificationStatus\", \"EndReason\", \"AppRuleVersion\", \"EconomyRuleVersion\", \"CreatedAtUtc\", \"UpdatedAtUtc\") VALUES " +
                "($pending, $profile, 'Balanced', 600, 'RecoverableInterrupted', '2026-11-14T09:00:00.0000000Z', NULL, 300, 300, 0, 0, 0, 0, 0, 'Unknown', 'Unverified', 'AgentCrash', 'APP-RULES-1', 'ECONOMY-1', '2026-11-14T09:00:00.0000000Z', '2026-11-14T09:05:00.0000000Z');" +
                "INSERT INTO \"FocusSession\" (\"Id\", \"LocalProfileId\", \"ProfileCode\", \"PlannedFocusSeconds\", \"State\", " +
                "\"StartedAtUtc\", \"LastCheckpointAtUtc\", \"ElapsedSeconds\", \"ActiveStudySeconds\", \"IdleSeconds\", " +
                "\"ProtectionGapSeconds\", \"ViolationCount\", \"ExceptionCount\", \"RecoveryCount\", \"ProtectionStatus\", " +
                "\"VerificationStatus\", \"EndReason\", \"AppRuleVersion\", \"EconomyRuleVersion\", \"CreatedAtUtc\", \"UpdatedAtUtc\", \"EndedAtUtc\") VALUES " +
                "($abandoned, $profile, 'Balanced', 600, 'Abandoned', '2026-11-13T09:00:00.0000000Z', '2026-11-13T09:05:00.0000000Z', 300, 300, 0, 0, 0, 0, 0, 'Unknown', 'Unverified', 'Unknown', 'APP-RULES-1', 'ECONOMY-1', '2026-11-13T09:00:00.0000000Z', '2026-11-13T09:10:00.0000000Z', '2026-11-13T09:10:00.0000000Z');" +
                "INSERT INTO \"SessionEvent\" (\"Id\", \"SessionType\", \"SessionId\", \"Sequence\", \"EventType\", \"OccurredAtUtc\", \"LocalDateAtEvent\", \"UtcOffsetMinutes\", \"PayloadJson\", \"CreatedAtUtc\") VALUES " +
                "($e1, 'Focus', $pending, 1, 'Interrupted', '2026-11-14T09:05:00.0000000Z', '2026-11-14', 0, '', '2026-11-14T09:05:00.0000000Z')," +
                "($e2, 'Focus', $abandoned, 1, 'Interrupted', '2026-11-13T09:05:00.0000000Z', '2026-11-13', 0, '', '2026-11-13T09:05:00.0000000Z')," +
                "($e3, 'Focus', $abandoned, 2, 'Abandoned', '2026-11-13T09:10:00.0000000Z', '2026-11-13', 0, $payload, '2026-11-13T09:10:00.0000000Z');";
            command.Parameters.AddWithValue("$pending", Stored(pendingSessionId));
            command.Parameters.AddWithValue("$abandoned", Stored(abandonedSessionId));
            command.Parameters.AddWithValue("$profile", Stored(profileId));
            command.Parameters.AddWithValue("$e1", Stored(Guid.NewGuid()));
            command.Parameters.AddWithValue("$e2", Stored(Guid.NewGuid()));
            command.Parameters.AddWithValue("$e3", Stored(Guid.NewGuid()));
            command.Parameters.AddWithValue("$payload", payloadAtLimit);
            await command.ExecuteNonQueryAsync();
        }

        var bootstrap = await db.Store.InitializeAsync();
        Assert.Equal(ThirdMigration, Assert.Single(bootstrap.Migration.PendingBeforeMigration));
        Assert.Equal(3, bootstrap.Migration.AppliedMigrations.Count);
        Assert.Equal(ThirdMigration, bootstrap.Migration.SchemaVersion);
        Assert.NotNull(bootstrap.Migration.PreMigrationBackup);
        Assert.Equal(SecondMigration, bootstrap.Migration.PreMigrationBackup!.SchemaVersion);
        Assert.Equal(1, await db.ScalarAsync(
            "SELECT count(*) FROM pragma_table_info('FocusSession') WHERE name = 'InterruptedAtUtc';"));

        await using var repositories = db.Store.CreateRepositories();
        var pending = await repositories.FocusSessions.FindAsync(pendingSessionId);
        Assert.NotNull(pending);
        Assert.Equal(SessionState.RecoverableInterrupted, pending.State);
        Assert.Null(pending.InterruptedAtUtc);
        Assert.Equal(pending.UpdatedAtUtc, pending.EffectiveInterruptionAnchorUtc);
        Assert.True(pending.IsPendingInterruptionExpired(pending.UpdatedAtUtc.AddHours(24)));

        var abandoned = await repositories.FocusSessions.FindAsync(abandonedSessionId);
        Assert.NotNull(abandoned);
        Assert.Equal(SessionState.Abandoned, abandoned.State);
        Assert.Null(abandoned.InterruptedAtUtc);

        var pendingEvents = await repositories.SessionEvents.ListBySessionAsync(SessionType.Focus, pendingSessionId);
        Assert.Equal(SessionEventType.Interrupted, Assert.Single(pendingEvents).EventType);

        var abandonedEvents = await repositories.SessionEvents.ListBySessionAsync(SessionType.Focus, abandonedSessionId);
        Assert.Equal(
            new[] { SessionEventType.Interrupted, SessionEventType.Abandoned },
            abandonedEvents.OrderBy(e => e.Sequence).Select(e => e.EventType));
        Assert.Equal(SessionEvent.MaxPayloadJsonLength, abandonedEvents.Single(e => e.EventType == SessionEventType.Abandoned).PayloadJson.Length);
    }

    [Fact]
    public async Task Unknown_newer_migration_is_rejected_before_a_backup_or_schema_write()
    {
        await using var db = await TestDatabase.CreateAsync();
        const string unknown = "20990101000000_FutureMigration";

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ($id, '10.0.15');";
            command.Parameters.AddWithValue("$id", unknown);
            await command.ExecuteNonQueryAsync();
        }

        var backupsBefore = Directory.Exists(db.Options.ResolvedBackupDirectory)
            ? Directory.GetFiles(db.Options.ResolvedBackupDirectory).Length
            : 0;

        var error = await Assert.ThrowsAsync<SchemaMismatchException>(
            () => db.Bootstrapper.InitializeAsync());

        Assert.Contains(unknown, error.Message, StringComparison.Ordinal);
        Assert.Equal(backupsBefore,
            Directory.Exists(db.Options.ResolvedBackupDirectory)
                ? Directory.GetFiles(db.Options.ResolvedBackupDirectory).Length
                : 0);

        await using var connectionAfter = await db.OpenConnectionAsync();
        await using var commandAfter = connectionAfter.CreateCommand();
        commandAfter.CommandText = "SELECT count(*) FROM __EFMigrationsHistory WHERE MigrationId = $id;";
        commandAfter.Parameters.AddWithValue("$id", unknown);
        Assert.Equal(1L, Convert.ToInt64(await commandAfter.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Killed_process_stale_lock_is_cleared_and_the_pending_migration_completes()
    {
        await using var db = await TestDatabase.CreateAsync(initialize: false);

        await CreateV1DatabaseAsync(db);
        await using (var seedContext = db.CreateContext())
        {
            await new SqliteDatabaseSeeder(db.Clock).SeedAsync(seedContext);
        }

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") " +
                "VALUES (1, '2026-11-14T09:00:00.0000000Z');";
            await command.ExecuteNonQueryAsync();
        }

        var bootstrap = await db.Store.InitializeAsync();

        Assert.NotNull(bootstrap.Migration.LockRecovery);
        Assert.True(bootstrap.Migration.LockRecovery.LockRowFound);
        Assert.False(bootstrap.Migration.LockRecovery.LiveWriterDetected);
        Assert.True(bootstrap.Migration.LockRecovery.StaleLockCleared);
        Assert.Equal(new[] { SecondMigration, ThirdMigration }, bootstrap.Migration.PendingBeforeMigration);
        Assert.NotNull(bootstrap.Migration.PreMigrationBackup);
        Assert.Equal(3, bootstrap.Migration.AppliedMigrations.Count);
        Assert.Equal(ThirdMigration, bootstrap.Migration.SchemaVersion);
        Assert.Equal(0, await db.ScalarAsync("SELECT count(*) FROM __EFMigrationsLock;"));
    }

    private static async Task CreateV1DatabaseAsync(TestDatabase db)
    {
        await using var context = db.CreateContext();
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(InitialMigration);
    }

    private static async Task CreateV2DatabaseAsync(TestDatabase db)
    {
        await using var context = db.CreateContext();
        var migrator = context.Database.GetService<IMigrator>();
        await migrator.MigrateAsync(SecondMigration);
    }

    private static async Task<string?> IntegrityCheckAsync(string filePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<string> Sha256Async(string filePath)
    {
        await using var stream = new FileStream(
            filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    }
}
