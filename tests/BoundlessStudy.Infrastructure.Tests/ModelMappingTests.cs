using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class ModelMappingTests
{
    private static readonly string[] ExpectedTables =
    [
        "AchievementDefinition",
        "AllowedSite",
        "AppSetting",
        "BackupRecord",
        "BlockedApp",
        "BlockedSite",
        "BreakSession",
        "FocusProfile",
        "FocusSession",
        "LocalProfile",
        "RewardLedgerEntry",
        "SessionEvent",
        "SessionException",
        "StudyTask",
        "Subject",
        "UserAchievement",
    ];

    [Fact]
    public async Task All_sixteen_entities_create_one_table_each()
    {
        await using var db = await TestDatabase.CreateAsync();

        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT LIKE '\\_\\_EF%' ESCAPE '\\' ORDER BY name;";

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(ExpectedTables, tables);
        Assert.Equal(16, tables.Count);
    }

    [Fact]
    public async Task Enums_are_stored_as_names_and_datetimes_round_trip_as_utc()
    {
        await using var db = await TestDatabase.CreateAsync();

        var strictMode = await db.TextAsync(
            "SELECT EarlyExitMode FROM FocusProfile WHERE Code = 'Strict';");
        Assert.Equal("TypedConfirmationCooldown", strictMode);

        var createdAt = new DateTime(2026, 11, 14, 10, 30, 0, DateTimeKind.Utc);
        await using var repositories = db.Store.CreateRepositories();
        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);

        var persisted = Subject.Create(profile.Id, "Physics", "Mechanics", createdAt);
        await repositories.Subjects.AddAsync(persisted);

        await using var context = db.CreateContext();
        var reloaded = await context.Subjects.SingleAsync(s => s.Id == persisted.Id);
        Assert.Equal(DateTimeKind.Utc, reloaded.CreatedAtUtc.Kind);
        Assert.Equal(createdAt, reloaded.CreatedAtUtc);

        var task = StudyTask.Create(
            profile.Id,
            "Read chapter 2",
            createdAt,
            dueDateLocal: new DateOnly(2026, 11, 20),
            plannedForLocalDate: new DateOnly(2026, 11, 15));
        await repositories.StudyTasks.AddAsync(task);

        await using var reloadContext = db.CreateContext();
        var reloadedTask = await reloadContext.StudyTasks.SingleAsync(t => t.Id == task.Id);
        Assert.Equal(new DateOnly(2026, 11, 20), reloadedTask.DueDateLocal);
        Assert.Equal(new DateOnly(2026, 11, 15), reloadedTask.PlannedForLocalDate);
    }

    [Fact]
    public async Task Check_constraints_reject_invalid_json_and_non_positive_ledger_amounts()
    {
        await using var db = await TestDatabase.CreateAsync();

        await using var connection = await db.OpenConnectionAsync();

        await using (var invalidJson = connection.CreateCommand())
        {
            invalidJson.CommandText =
                "INSERT INTO AppSetting (Key, ValueJson, UpdatedAtUtc) " +
                "VALUES ('bad', 'not-json', '2026-11-14T09:00:00.0000000Z');";
            var ex = await Assert.ThrowsAsync<SqliteException>(() => invalidJson.ExecuteNonQueryAsync());
            Assert.Contains("CHECK constraint failed", ex.Message);
        }

        var profileId = await db.TextAsync("SELECT Id FROM LocalProfile LIMIT 1;");
        await using (var invalidAmount = connection.CreateCommand())
        {
            invalidAmount.CommandText =
                "INSERT INTO RewardLedgerEntry " +
                "(Id, LocalProfileId, CurrencyCode, Amount, SourceType, SourceId, IdempotencyKey, " +
                "AwardedAtUtc, AwardLocalDate, UtcOffsetMinutes, EconomyRuleVersion, CreatedAtUtc) VALUES " +
                "($id, $profile, 'XP', 0, 'SessionCompletion', 's', 'k', " +
                "'2026-11-14T09:00:00.0000000Z', '2026-11-14', 0, 'ECONOMY-1', '2026-11-14T09:00:00.0000000Z');";
            invalidAmount.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
            invalidAmount.Parameters.AddWithValue("$profile", profileId);
            var ex = await Assert.ThrowsAsync<SqliteException>(() => invalidAmount.ExecuteNonQueryAsync());
            Assert.Contains("CHECK constraint failed", ex.Message);
        }
    }

    [Fact]
    public async Task Foreign_keys_are_enforced_on_every_connection()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var connection = await db.OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO Subject (Id, LocalProfileId, Name, Description, IsArchived, CreatedAtUtc, UpdatedAtUtc) " +
            "VALUES ($id, $profile, 'Orphan', '', 0, '2026-11-14T09:00:00.0000000Z', '2026-11-14T09:00:00.0000000Z');";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$profile", Guid.NewGuid().ToString());

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("FOREIGN KEY constraint failed", ex.Message);
    }

    [Fact]
    public async Task Filtered_unique_indexes_allow_only_one_active_focus_and_break_per_profile()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);

        var first = FocusSession.Start(
            profile.Id, "Balanced", 300, db.Clock.UtcNow, EconomyRules.Version);
        await repositories.FocusSessions.AddAsync(first);

        var second = FocusSession.Start(
            profile.Id, "Balanced", 300, db.Clock.UtcNow.AddMinutes(1), EconomyRules.Version);
        await Assert.ThrowsAsync<DbUpdateException>(() => repositories.FocusSessions.AddAsync(second));

        await using var breakSet = db.Store.CreateRepositories();
        var firstBreak = BreakSession.Start(profile.Id, 300, db.Clock.UtcNow);
        await breakSet.BreakSessions.AddAsync(firstBreak);

        var secondBreak = BreakSession.Start(profile.Id, 300, db.Clock.UtcNow.AddMinutes(1));
        await Assert.ThrowsAsync<DbUpdateException>(() => breakSet.BreakSessions.AddAsync(secondBreak));
    }

    [Fact]
    public async Task Session_event_sequence_is_unique_per_session_and_payload_is_bounded()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var sessionId = Guid.NewGuid();
        var occurredAt = db.Clock.UtcNow;
        var localDate = DateOnly.FromDateTime(occurredAt);

        var start = SessionEvent.Create(
            SessionType.Focus, sessionId, 1, SessionEventType.Start,
            occurredAt, localDate, 0, string.Empty, occurredAt);
        await repositories.SessionEvents.AddAsync(start);

        var duplicate = SessionEvent.Create(
            SessionType.Focus, sessionId, 1, SessionEventType.Blocked,
            occurredAt, localDate, 0, string.Empty, occurredAt);
        await Assert.ThrowsAsync<DbUpdateException>(() => repositories.SessionEvents.AddAsync(duplicate));

        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO SessionEvent (Id, SessionType, SessionId, Sequence, EventType, OccurredAtUtc, " +
            "LocalDateAtEvent, UtcOffsetMinutes, PayloadJson, CreatedAtUtc) VALUES " +
            "($id, 'Focus', $session, 9, 'Start', '2026-11-14T09:00:00.0000000Z', '2026-11-14', 0, $payload, " +
            "'2026-11-14T09:00:00.0000000Z');";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$session", sessionId.ToString());
        command.Parameters.AddWithValue("$payload", new string('a', SessionEvent.MaxPayloadJsonLength + 1));

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public async Task Session_event_payload_of_exactly_2000_characters_is_stored_and_readable()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var sessionId = Guid.NewGuid();
        var occurredAt = db.Clock.UtcNow;
        var payload = new string('p', SessionEvent.MaxPayloadJsonLength);

        var atLimit = SessionEvent.Create(
            SessionType.Focus, sessionId, 1, SessionEventType.Abandoned,
            occurredAt, DateOnly.FromDateTime(occurredAt), 0, payload, occurredAt);
        await repositories.SessionEvents.AddAsync(atLimit);

        await using var verification = db.Store.CreateRepositories();
        var stored = Assert.Single(await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId));
        Assert.Equal(SessionEvent.MaxPayloadJsonLength, stored.PayloadJson.Length);
        Assert.Equal(payload, stored.PayloadJson);
    }

    [Fact]
    public async Task Interrupted_and_abandoned_event_types_round_trip_and_unknown_types_are_rejected()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repositories = db.Store.CreateRepositories();
        var sessionId = Guid.NewGuid();
        var occurredAt = db.Clock.UtcNow;

        await repositories.SessionEvents.AddAsync(SessionEvent.Create(
            SessionType.Focus, sessionId, 1, SessionEventType.Interrupted,
            occurredAt, DateOnly.FromDateTime(occurredAt), 0, "", occurredAt));
        await repositories.SessionEvents.AddAsync(SessionEvent.Create(
            SessionType.Focus, sessionId, 2, SessionEventType.Abandoned,
            occurredAt.AddMinutes(1), DateOnly.FromDateTime(occurredAt), 0, "", occurredAt));

        await using var verification = db.Store.CreateRepositories();
        var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId);
        Assert.Equal(
            new[] { SessionEventType.Interrupted, SessionEventType.Abandoned },
            events.OrderBy(e => e.Sequence).Select(e => e.EventType));

        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO SessionEvent (Id, SessionType, SessionId, Sequence, EventType, OccurredAtUtc, " +
            "LocalDateAtEvent, UtcOffsetMinutes, PayloadJson, CreatedAtUtc) VALUES " +
            "($id, 'Focus', $session, 3, 'NotAnEventType', '2026-11-14T09:00:00.0000000Z', '2026-11-14', 0, '', " +
            "'2026-11-14T09:00:00.0000000Z');";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString().ToUpperInvariant());
        command.Parameters.AddWithValue("$session", sessionId.ToString().ToUpperInvariant());

        var ex = await Assert.ThrowsAsync<SqliteException>(() => command.ExecuteNonQueryAsync());
        Assert.Contains("CHECK constraint failed", ex.Message);
    }

    [Fact]
    public async Task Checkpoint_elapsed_is_computed_from_persisted_columns_not_duplicated()
    {
        await using var db = await TestDatabase.CreateAsync();

        await using (var connection = await db.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('FocusSession');";
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(1));
            }

            Assert.Contains("LastCheckpointAtUtc", columns);
            Assert.DoesNotContain("LastCheckpointElapsedSeconds", columns);
        }

        await using var repositories = db.Store.CreateRepositories();
        var profile = await repositories.LocalProfiles.FindSingleAsync();
        Assert.NotNull(profile);

        var session = FocusSession.Start(
            profile.Id, "Balanced", 300, db.Clock.UtcNow, EconomyRules.Version);
        session.ApplyCheckpoint(db.Clock.UtcNow.AddMinutes(5), 300, 280, 20, 0, 0, 0, 0);
        await repositories.FocusSessions.AddAsync(session);

        await using var reloadContext = db.CreateContext();
        var reloaded = await reloadContext.FocusSessions.SingleAsync(s => s.Id == session.Id);
        Assert.Equal(300, reloaded.LastCheckpointElapsedSeconds);
    }
}
