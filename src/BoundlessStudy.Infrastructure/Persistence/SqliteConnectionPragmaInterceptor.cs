using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace BoundlessStudy.Infrastructure.Persistence;

/// <summary>
/// Applies per-connection SQLite pragmas. <c>foreign_keys</c> is per connection and
/// must be set on every pooled physical connection; the bounded busy timeout is
/// part of ADR-0005's no-unbounded-wait rule.
/// </summary>
public sealed class SqliteConnectionPragmaInterceptor : DbConnectionInterceptor
{
    private readonly int _busyTimeoutMilliseconds;

    public SqliteConnectionPragmaInterceptor(int busyTimeoutMilliseconds)
    {
        _busyTimeoutMilliseconds = busyTimeoutMilliseconds;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection, _busyTimeoutMilliseconds);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPragmasAsync(connection, _busyTimeoutMilliseconds, cancellationToken);
    }

    /// <summary>
    /// Synchronously applies required pragmas (foreign keys, synchronous, busy timeout) to a connection.
    /// </summary>
    public static void ApplyPragmas(DbConnection connection, int busyTimeoutMilliseconds)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {busyTimeoutMilliseconds}; PRAGMA synchronous = NORMAL;";
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Asynchronously applies required pragmas (foreign keys, synchronous, busy timeout) to a connection.
    /// </summary>
    public static async Task ApplyPragmasAsync(
        DbConnection connection,
        int busyTimeoutMilliseconds,
        CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"PRAGMA foreign_keys = ON; PRAGMA busy_timeout = {busyTimeoutMilliseconds}; PRAGMA synchronous = NORMAL;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
