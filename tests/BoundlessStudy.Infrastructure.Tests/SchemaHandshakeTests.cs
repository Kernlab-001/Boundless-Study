using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class SchemaHandshakeTests
{
    [Fact]
    public async Task Up_to_date_database_passes_the_handshake_and_reports_its_schema_version()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var context = db.CreateContext();

        var result = await SchemaHandshake.VerifyAsync(context);

        Assert.Equal(3, result.CompiledMigrations.Count);
        Assert.Equal(result.CompiledMigrations, result.AppliedMigrations);
        Assert.EndsWith("_AddFocusSessionInterruptedAtUtc", result.SchemaVersion, StringComparison.Ordinal);
        Assert.Equal(result.SchemaVersion, SchemaHandshake.CurrentSchemaVersion(context));
    }

    [Fact]
    public async Task Database_with_an_unknown_applied_migration_is_rejected_as_newer()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ('20990101000000_FutureSchema', '99.0.0');";
            await command.ExecuteNonQueryAsync();
        }

        await using var context = db.CreateContext();
        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(
            () => SchemaHandshake.VerifyAsync(context));

        Assert.Equal("SCHEMA_VERSION_MISMATCH", ex.Code);
        Assert.Contains("newer", ex.Message);
        Assert.Contains("20990101000000_FutureSchema", ex.Message);
    }

    [Fact]
    public async Task Database_missing_an_applied_migration_is_rejected_as_older()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM \"__EFMigrationsHistory\";";
            await command.ExecuteNonQueryAsync();
        }

        await using var context = db.CreateContext();
        var ex = await Assert.ThrowsAsync<SchemaMismatchException>(
            () => SchemaHandshake.VerifyAsync(context));

        Assert.Equal("SCHEMA_VERSION_MISMATCH", ex.Code);
        Assert.Contains("older", ex.Message);
        Assert.Contains("InitialCreate", ex.Message);
    }
}
