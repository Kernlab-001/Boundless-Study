using BoundlessStudy.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence;

public sealed record SchemaHandshakeResult(
    IReadOnlyList<string> CompiledMigrations,
    IReadOnlyList<string> AppliedMigrations)
{
    public string SchemaVersion => CompiledMigrations.Count == 0
        ? "none"
        : CompiledMigrations[^1];
}

/// <summary>
/// Compares the applied <c>__EFMigrationsHistory</c> rows with the migrations
/// compiled into this binary before any normal query is served (R-P3-14).
/// A database newer than the application and a database older than the
/// application are both rejected; silent data loss or silent writes against an
/// unknown schema are never acceptable (conceptual-data-model.md §8).
/// </summary>
public static class SchemaHandshake
{
    public static async Task<SchemaHandshakeResult> VerifyAsync(
        BoundlessDbContext context,
        CancellationToken cancellationToken = default)
    {
        var compiled = context.Database.GetMigrations().ToArray();
        var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToArray();

        var missing = compiled.Except(applied, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new SchemaMismatchException(
                "The database schema is older than the application. Pending migrations: " +
                string.Join(", ", missing) + ".");
        }

        var unknown = applied.Except(compiled, StringComparer.Ordinal).ToArray();
        if (unknown.Length > 0)
        {
            throw new SchemaMismatchException(
                "The database schema is newer than this application. Unknown migrations: " +
                string.Join(", ", unknown) + ". Install the newer application or restore a compatible backup.");
        }

        return new SchemaHandshakeResult(compiled, applied);
    }

    /// <summary>The schema version label recorded on backups and diagnostics.</summary>
    public static string CurrentSchemaVersion(BoundlessDbContext context) =>
        context.Database.GetMigrations().LastOrDefault() ?? "none";
}
