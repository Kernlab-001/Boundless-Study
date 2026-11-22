using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

internal sealed class TestLocalClock : ILocalClock
{
    public DateTime UtcNow { get; set; } = new(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc);

    public int UtcOffsetMinutes { get; set; }

    public DateOnly ToLocalDate(DateTime utcTime) =>
        DateOnly.FromDateTime(DateTime.SpecifyKind(utcTime, DateTimeKind.Utc).AddMinutes(UtcOffsetMinutes));
}

/// <summary>
/// A disposable temporary database. Every test gets its own directory, database,
/// and backup folder; nothing outside the temporary test root is touched.
/// </summary>
internal sealed class TestDatabase : IAsyncDisposable
{
    private TestDatabase(string root, SqliteDatabaseOptions options, TestLocalClock clock, SqliteDataStore store)
    {
        Root = root;
        Options = options;
        Clock = clock;
        Store = store;
    }

    public string Root { get; }

    public SqliteDatabaseOptions Options { get; }

    public TestLocalClock Clock { get; }

    public SqliteDataStore Store { get; }

    public SqliteDatabaseBootstrapper Bootstrapper => new(Options, Clock);

    public static async Task<TestDatabase> CreateAsync(bool initialize = true)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "BoundlessStudy.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var options = new SqliteDatabaseOptions
        {
            DatabasePath = Path.Combine(root, PersistenceConstants.DatabaseFileName),
            BackupDirectory = Path.Combine(root, PersistenceConstants.BackupFolderName),
            Pooling = false,
            BusyTimeoutMilliseconds = 5_000,
            MigrationMutexWaitMilliseconds = 5_000,
            MigrationLockProbeTimeoutMilliseconds = 250,
            AutomaticBackupRetentionCount = 7,
        };

        var clock = new TestLocalClock();
        var store = new SqliteDataStore(options, clock);
        if (initialize)
        {
            await store.InitializeAsync();
        }

        return new TestDatabase(root, options, clock, store);
    }

    public BoundlessDbContext CreateContext() => new(Options.BuildDbContextOptions());

    public async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(Options.BuildConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    public async Task<long> ScalarAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task<string?> TextAsync(string sql)
    {
        await using var connection = await OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync());
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        SqliteConnection.ClearAllPools();

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leftover temporary directory must not fail the test run.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
