using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Tests;

/// <summary>
/// Independent real-SQLite proof for the cross-connection write serialization.
///
/// Two concurrency models are exercised and kept separate:
/// <list type="bullet">
/// <item><b>Multi-connection, single process</b>: every racer owns its own
/// <see cref="SqliteDataStore"/>, repository scope, and SQLite connection. These
/// tests prove in-process serialization, transaction atomicity, and the one-active
/// invariant under real concurrent transactions.</item>
/// <item><b>Multi-process</b>: <see cref="Cross_process_write_gate_times_out_then_succeeds_after_release"/>
/// and <see cref="Killed_child_process_holding_an_immediate_transaction_rolls_back"/>
/// launch a real child process running Microsoft.Data.Sqlite against the same file,
/// holding the named write gate and a BEGIN IMMEDIATE transaction.</item>
/// </list>
///
/// Mutating handlers are invoked through <see cref="CommandDispatcher"/>, the
/// documented production entry point. The dispatcher opens the outermost
/// transaction before the handler performs its reads, so the read-check-write
/// sequence is serialized as a whole rather than only the final INSERT.
/// </summary>
public sealed class MultiProcessConcurrencyTests
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);

    private static readonly SessionState[] ActiveStates =
    [
        SessionState.Active,
        SessionState.Blocked,
        SessionState.SystemPaused,
        SessionState.RecoverableInterrupted,
    ];

    [Fact]
    public async Task Simultaneous_start_in_independent_stores_allows_exactly_one_active_session()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        const int racers = 4;

        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 5_000);
        Attempt<EntityResult>[] attempts;
        try
        {
            var actions = new List<Func<Task<EntityResult>>>(racers);
            foreach (var store in stores)
            {
                var currentStore = store;
                actions.Add(async () =>
                {
                    await using var repositories = currentStore.CreateRepositories();
                    var start = StartHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 600, cancellationToken: ct));
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        AssertNoFailures(attempts);
        var successes = attempts.Where(a => a.Value!.Success).ToArray();
        Assert.True(
            successes.Length == 1,
            $"Expected exactly one winning start, observed {successes.Length}.{Environment.NewLine}{Describe(attempts)}");

        foreach (var loser in attempts.Where(a => !a.Value!.Success))
        {
            Assert.Equal(UseCaseErrorCodes.ActiveFocusSessionExists, loser.Value!.Error!.Code);
        }

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        Assert.Equal(1, await CountActiveSessionsAsync(verification, profileId));
        Assert.Equal(1, await verification.Context.FocusSessions.CountAsync(s => s.LocalProfileId == profileId));
        Assert.Equal(
            1,
            await verification.Context.SessionEvents.CountAsync(
                e => e.SessionType == SessionType.Focus && e.EventType == SessionEventType.Start));
    }

    [Fact]
    public async Task Resume_and_discard_race_commits_exactly_one_terminal_transition_and_one_reward()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var seeded = await SeedInterruptedSessionAsync(
            db,
            profileId,
            plannedFocusSeconds: 600,
            elapsedSeconds: 600,
            activeStudySeconds: 600);

        var stores = await CreateIndependentStoresAsync(db, 2, busyTimeoutMilliseconds: 5_000);
        Attempt<SessionCompletionOutcome>[] attempts;
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recoverStore = stores[0];
            var discardStore = stores[1];

            var recoverTask = RunAfterAsync(gate.Task, async () =>
            {
                await using var repositories = recoverStore.CreateRepositories();
                var recover = RecoveryHandler(repositories, db.Clock);
                return await CaptureAsync(() => new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                    ct => recover.HandleAsync(seeded.SessionId, ct)));
            });

            var discardTask = RunAfterAsync(gate.Task, async () =>
            {
                await using var repositories = discardStore.CreateRepositories();
                var discard = AbandonHandler(repositories, db.Clock);
                return await CaptureAsync(() => new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                    ct => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown, ct)));
            });

            gate.SetResult();
            attempts = await Task.WhenAll(recoverTask, discardTask).WaitAsync(OperationTimeout);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        AssertNoFailures(attempts);
        Assert.All(attempts, a => Assert.True(a.Value!.Success, a.Value.Error?.Message));
        Assert.True(
            attempts.Count(a => a.Value!.Changed) == 1,
            $"Exactly one racer must change the session.{Environment.NewLine}{Describe(attempts)}");

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        var session = await verification.FocusSessions.FindAsync(seeded.SessionId);
        Assert.NotNull(session);
        Assert.True(
            session!.State is SessionState.Completed or SessionState.Abandoned,
            $"Expected one terminal state, observed {session.State}.");

        var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
        AssertUniqueEventSequences(events);
        Assert.Single(events, e => e.EventType is SessionEventType.Completed or SessionEventType.Abandoned);
        Assert.Single(events, e => e.EventType == SessionEventType.RewardCreated);

        var ledger = await verification.RewardLedger.ListByProfileAsync(profileId);
        var sessionEntries = ledger
            .Where(e => string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var entry = Assert.Single(sessionEntries);
        Assert.Equal(
            session.State == SessionState.Completed
                ? LedgerSourceType.SessionCompletion
                : LedgerSourceType.SessionPartial,
            entry.SourceType);
        Assert.Equal(0, await CountActiveSessionsAsync(verification, profileId));
    }

    [Fact]
    public async Task Simultaneous_duplicate_discard_awards_one_partial_and_writes_one_abandoned_event()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var seeded = await SeedInterruptedSessionAsync(
            db,
            profileId,
            plannedFocusSeconds: 1_500,
            elapsedSeconds: 600,
            activeStudySeconds: 600);
        const int racers = 4;

        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 5_000);
        Attempt<SessionCompletionOutcome>[] attempts;
        try
        {
            var actions = new List<Func<Task<SessionCompletionOutcome>>>(racers);
            foreach (var store in stores)
            {
                var currentStore = store;
                actions.Add(async () =>
                {
                    await using var repositories = currentStore.CreateRepositories();
                    var discard = AbandonHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown, ct));
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        AssertNoFailures(attempts);
        Assert.All(attempts, a => Assert.True(a.Value!.Success, a.Value.Error?.Message));
        Assert.True(
            attempts.Count(a => a.Value!.Changed) == 1,
            $"Exactly one discard may change the session.{Environment.NewLine}{Describe(attempts)}");

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        var session = await verification.FocusSessions.FindAsync(seeded.SessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Abandoned, session!.State);
        Assert.Equal(0, await CountActiveSessionsAsync(verification, profileId));

        var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
        AssertUniqueEventSequences(events);
        Assert.Single(events, e => e.EventType == SessionEventType.Abandoned);
        Assert.Single(events, e => e.EventType == SessionEventType.RewardCreated);

        var ledger = await verification.RewardLedger.ListByProfileAsync(profileId);
        var partial = Assert.Single(
            ledger,
            e => e.SourceType == LedgerSourceType.SessionPartial
                && string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase));

        // The failed/finished transactions released their connections and gate; a
        // fresh command must be able to write immediately.
        var subject = await CreateSubjectAsync(fresh, profileId, "after-duplicate-discard");
        Assert.True(subject.Success, subject.Error?.Message);
        Assert.True(partial.Amount > 0);
    }

    [Fact]
    public async Task Expiry_boundary_start_versus_discard_leaves_one_abandoned_old_one_active_new_and_one_partial()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var seeded = await SeedInterruptedSessionAsync(
            db,
            profileId,
            plannedFocusSeconds: 1_500,
            elapsedSeconds: 600,
            activeStudySeconds: 600);

        // Exactly 24:00:00 after the interruption: the boundary is inclusive.
        db.Clock.UtcNow = seeded.InterruptedAtUtc.AddHours(24);

        var stores = await CreateIndependentStoresAsync(db, 2, busyTimeoutMilliseconds: 5_000);
        Attempt<EntityResult> startAttempt;
        Attempt<SessionCompletionOutcome> discardAttempt;
        try
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var startStore = stores[0];
            var discardStore = stores[1];

            var startTask = RunAfterAsync(gate.Task, async () =>
            {
                await using var repositories = startStore.CreateRepositories();
                var start = StartHandler(repositories, db.Clock);
                return await CaptureAsync(() => new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                    ct => start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1_500, cancellationToken: ct)));
            });

            var discardTask = RunAfterAsync(gate.Task, async () =>
            {
                await using var repositories = discardStore.CreateRepositories();
                var discard = AbandonHandler(repositories, db.Clock);
                return await CaptureAsync(() => new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                    ct => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown, ct)));
            });

            gate.SetResult();
            await Task.WhenAll(startTask, discardTask).WaitAsync(OperationTimeout);
            startAttempt = await startTask;
            discardAttempt = await discardTask;
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        Assert.Null(startAttempt.Failure);
        Assert.Null(discardAttempt.Failure);
        Assert.True(startAttempt.Value!.Success, startAttempt.Value.Error?.Message);
        Assert.True(discardAttempt.Value!.Success, discardAttempt.Value.Error?.Message);

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        var sessions = await verification.FocusSessions.ListByProfileAsync(profileId);
        Assert.Equal(2, sessions.Count);

        var oldSession = Assert.Single(sessions, s => s.Id == seeded.SessionId);
        Assert.Equal(SessionState.Abandoned, oldSession.State);
        Assert.Equal(db.Clock.UtcNow, oldSession.EndedAtUtc);

        var newSession = Assert.Single(sessions, s => s.Id != seeded.SessionId);
        Assert.Equal(SessionState.Active, newSession.State);

        var active = await verification.Context.FocusSessions
            .Where(s => s.LocalProfileId == profileId && ActiveStates.Contains(s.State))
            .ToListAsync();
        Assert.Equal(newSession.Id, Assert.Single(active).Id);

        var oldEvents = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
        AssertUniqueEventSequences(oldEvents);
        Assert.Single(oldEvents, e => e.EventType == SessionEventType.Abandoned);
        Assert.Single(oldEvents, e => e.EventType == SessionEventType.RewardCreated);

        var ledger = await verification.RewardLedger.ListByProfileAsync(profileId);
        Assert.Single(
            ledger,
            e => e.SourceType == LedgerSourceType.SessionPartial
                && string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Rollback_after_a_saved_session_transition_leaves_no_partial_rows_and_database_usable()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var seeded = await SeedInterruptedSessionAsync(
            db,
            profileId,
            plannedFocusSeconds: 1_500,
            elapsedSeconds: 600,
            activeStudySeconds: 600);

        // Phase 1: a real UPDATE reaches SQLite inside the transaction, then the
        // unit of work is rolled back. Nothing may remain.
        await using (var repositories = db.Store.CreateRepositories())
        {
            await repositories.UnitOfWork.BeginAsync();
            var tracked = await repositories.FocusSessions.FindAsync(seeded.SessionId);
            Assert.NotNull(tracked);
            tracked!.Abandon(db.Clock.UtcNow, FocusEndReason.Unknown);
            await repositories.Context.SaveChangesAsync();
            Assert.NotNull(repositories.Context.Database.CurrentTransaction);
            await repositories.UnitOfWork.RollbackAsync();
            Assert.Null(repositories.Context.Database.CurrentTransaction);
        }

        await using (var checkStore = await OpenFreshStoreAsync(db))
        await using (var check = checkStore.CreateRepositories())
        {
            var session = await check.FocusSessions.FindAsync(seeded.SessionId);
            Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
            var events = await check.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
            Assert.DoesNotContain(events, e => e.EventType == SessionEventType.Abandoned);
            Assert.DoesNotContain(
                await check.RewardLedger.ListByProfileAsync(profileId),
                e => string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        // Phase 2: the terminal event is written, then the injected ledger failure
        // aborts the command. The session transition and event must roll back with
        // the reward; the exception must not leave an open transaction behind.
        await using (var repositories = db.Store.CreateRepositories())
        {
            var failingLedger = new ThrowingRewardLedgerRepository(repositories.RewardLedger);
            var rewards = new RewardService(
                failingLedger,
                repositories.Achievements,
                repositories.UserAchievements,
                repositories.RewardState,
                db.Clock);
            var recovery = new RecoverFocusSessionHandler(
                repositories.FocusSessions,
                repositories.LocalProfiles,
                repositories.UnitOfWork,
                rewards,
                db.Clock,
                new SessionEventRecorder(repositories.SessionEvents, db.Clock));
            var discard = new AbandonFocusSessionHandler(repositories.FocusSessions, db.Clock, recovery);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown));
            Assert.Null(repositories.Context.Database.CurrentTransaction);
        }

        await using (var checkStore = await OpenFreshStoreAsync(db))
        await using (var check = checkStore.CreateRepositories())
        {
            var session = await check.FocusSessions.FindAsync(seeded.SessionId);
            Assert.Equal(SessionState.RecoverableInterrupted, session!.State);
            var events = await check.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
            Assert.DoesNotContain(events, e => e.EventType == SessionEventType.Abandoned);
            Assert.DoesNotContain(events, e => e.EventType == SessionEventType.RewardCreated);
            Assert.DoesNotContain(
                await check.RewardLedger.ListByProfileAsync(profileId),
                e => string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        // The database remains fully usable and a later real discard commits once.
        await using (var repositories = db.Store.CreateRepositories())
        {
            var discard = AbandonHandler(repositories, db.Clock);
            var outcome = await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                ct => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown, ct));
            Assert.True(outcome.Success, outcome.Error?.Message);
            Assert.True(outcome.Changed);
        }

        await using var final = await OpenFreshStoreAsync(db);
        await using var finalRepositories = final.CreateRepositories();
        Assert.Equal(SessionState.Abandoned, (await finalRepositories.FocusSessions.FindAsync(seeded.SessionId))!.State);
        Assert.Single(
            await finalRepositories.RewardLedger.ListByProfileAsync(profileId),
            e => e.SourceType == LedgerSourceType.SessionPartial
                && string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Cross_process_write_gate_times_out_then_succeeds_after_release()
    {
        await using var db = await TestDatabase.CreateAsync();
        await CreateProbeTableAsync(db);
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var options = db.Options with { BusyTimeoutMilliseconds = 500 };
        await using var store = new SqliteDataStore(options, db.Clock);
        await store.InitializeAsync();

        var readyFile = Path.Combine(db.Root, "child-ready.txt");
        var releaseFile = Path.Combine(db.Root, "child-release.txt");

        await using var child = await ChildWriterProcess.StartAsync(options.DatabasePath, readyFile, releaseFile);
        var elapsed = Stopwatch.StartNew();
        var timeout = await Assert.ThrowsAsync<WriteGateTimeoutException>(
            () => CreateSubjectAsync(store, profileId, "must-not-write"));
        elapsed.Stop();

        Assert.Equal("WRITE_GATE_TIMEOUT", timeout.Code);
        Assert.InRange(elapsed.ElapsedMilliseconds, 250, 10_000);

        await using (var afterTimeout = store.CreateRepositories())
        {
            Assert.Null(afterTimeout.Context.Database.CurrentTransaction);
            Assert.Empty(await afterTimeout.Subjects.ListAsync(profileId, includeArchived: true));
        }

        await child.ReleaseAndWaitAsync();
        Assert.Equal("child-uncommitted", await db.TextAsync("SELECT \"Value\" FROM \"__ConcurrencyProbe\" WHERE \"Id\" = 1;"));

        var retry = await CreateSubjectAsync(store, profileId, "after-child-release");
        Assert.True(retry.Success, retry.Error?.Message);

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        Assert.Single(await verification.Subjects.ListAsync(profileId, includeArchived: true));
        Assert.Equal("ok", await db.TextAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task Killed_child_process_holding_an_immediate_transaction_rolls_back_and_next_write_succeeds()
    {
        await using var db = await TestDatabase.CreateAsync();
        await CreateProbeTableAsync(db);
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var options = db.Options with { BusyTimeoutMilliseconds = 5_000 };
        await using var store = new SqliteDataStore(options, db.Clock);
        await store.InitializeAsync();

        var readyFile = Path.Combine(db.Root, "child-ready.txt");
        var releaseFile = Path.Combine(db.Root, "child-release.txt");

        await using (var child = await ChildWriterProcess.StartAsync(options.DatabasePath, readyFile, releaseFile))
        {
            await child.KillAsync();
        }

        // The killed process never committed its inserted row.
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"__ConcurrencyProbe\";"));

        // The abandoned named mutex is taken over and a normal write succeeds.
        var write = await CreateSubjectAsync(store, profileId, "after-child-crash");
        Assert.True(write.Success, write.Error?.Message);

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        Assert.Single(await verification.Subjects.ListAsync(profileId, includeArchived: true));
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"__ConcurrencyProbe\";"));
        Assert.Equal("ok", await db.TextAsync("PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task Simultaneous_start_resume_discard_and_auto_abandonment_race_on_expired_session_leaves_consistent_state()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var seeded = await SeedInterruptedSessionAsync(
            db,
            profileId,
            plannedFocusSeconds: 1_500,
            elapsedSeconds: 600,
            activeStudySeconds: 600);

        // Advance clock past the 24-hour expiry threshold so the interrupted session is expired.
        db.Clock.UtcNow = seeded.InterruptedAtUtc.AddHours(25);

        const int racers = 6;
        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 10_000);
        Attempt<ICommandOutcome>[] attempts;
        try
        {
            var actions = new List<Func<Task<ICommandOutcome>>>(racers);

            // Racers 0 & 1: Start new focus session (triggers auto-abandonment of expired session, then starts new session)
            for (var i = 0; i < 2; i++)
            {
                var store = stores[i];
                actions.Add(async () =>
                {
                    await using var repositories = store.CreateRepositories();
                    var start = StartHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1_500, cancellationToken: ct));
                });
            }

            // Racers 2 & 3: Recover focus session (detects expired -> abandons expired session)
            for (var i = 2; i < 4; i++)
            {
                var store = stores[i];
                actions.Add(async () =>
                {
                    await using var repositories = store.CreateRepositories();
                    var recover = RecoveryHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => recover.HandleAsync(seeded.SessionId, ct));
                });
            }

            // Racers 4 & 5: Discard focus session (explicitly abandons expired session)
            for (var i = 4; i < 6; i++)
            {
                var store = stores[i];
                actions.Add(async () =>
                {
                    await using var repositories = store.CreateRepositories();
                    var discard = AbandonHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown, ct));
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        AssertNoFailures(attempts);

        // Verification on a fresh store
        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();

        // 1. Old session is strictly in Abandoned state with non-null EndedAtUtc
        var oldSession = await verification.FocusSessions.FindAsync(seeded.SessionId);
        Assert.NotNull(oldSession);
        Assert.Equal(SessionState.Abandoned, oldSession!.State);
        Assert.NotNull(oldSession.EndedAtUtc);

        // 2. Exactly one start succeeded and created a new session
        var startAttempts = attempts.Take(2).ToArray();
        var startSuccesses = startAttempts.Where(a => a.Value!.Success).ToArray();
        Assert.Single(startSuccesses);
        var winningStart = (EntityResult)startSuccesses[0].Value!;
        var winningNewSessionId = winningStart.Id!.Value;

        // The losing start attempted when an active session already existed
        var losingStart = (EntityResult)Assert.Single(startAttempts, a => !a.Value!.Success).Value!;
        Assert.Equal(UseCaseErrorCodes.ActiveFocusSessionExists, losingStart.Error!.Code);

        // 3. The new session is strictly in Active state
        var newSession = await verification.FocusSessions.FindAsync(winningNewSessionId);
        Assert.NotNull(newSession);
        Assert.Equal(SessionState.Active, newSession!.State);

        // 4. Exactly one active session exists for this profile
        Assert.Equal(1, await CountActiveSessionsAsync(verification, profileId));

        // 5. Total focus sessions in DB for this profile is exactly 2 (old abandoned + new active)
        var allSessions = await verification.FocusSessions.ListByProfileAsync(profileId);
        Assert.Equal(2, allSessions.Count);

        // 6. Old session events: exactly one Abandoned, exactly one RewardCreated, distinct sequence numbers
        var oldEvents = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
        AssertUniqueEventSequences(oldEvents);
        Assert.Single(oldEvents, e => e.EventType == SessionEventType.Abandoned);
        Assert.Single(oldEvents, e => e.EventType == SessionEventType.RewardCreated);

        // 7. New session events: exactly one Start event
        var newEvents = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, winningNewSessionId);
        AssertUniqueEventSequences(newEvents);
        Assert.Single(newEvents, e => e.EventType == SessionEventType.Start);

        // 8. Reward ledger: exactly one SessionPartial entry for old session
        var ledger = await verification.RewardLedger.ListByProfileAsync(profileId);
        var oldSessionEntries = ledger
            .Where(e => string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var partialEntry = Assert.Single(oldSessionEntries);
        Assert.Equal(LedgerSourceType.SessionPartial, partialEntry.SourceType);
        Assert.True(partialEntry.Amount > 0);

        // 9. Profile cached totals match sum of ledger XP
        var profile = await verification.LocalProfiles.FindAsync(profileId);
        Assert.NotNull(profile);
        var totalXp = await verification.RewardLedger.SumXpAsync(profileId);
        Assert.Equal(totalXp, profile!.CachedTotalXp);

        // 10. Database integrity
        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task Simultaneous_start_resume_and_discard_race_on_unexpired_checkpointed_session_preserves_single_terminal_reward()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var seeded = await SeedInterruptedSessionAsync(
            db,
            profileId,
            plannedFocusSeconds: 600,
            elapsedSeconds: 600,
            activeStudySeconds: 600);

        // Clock is advanced by only 10 minutes: session is NOT expired.
        // Because elapsed reached planned (600s), a resume will complete it, while a discard will abandon it.
        const int racers = 4;
        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 10_000);
        Attempt<SessionCompletionOutcome>[] attempts;
        try
        {
            var actions = new List<Func<Task<SessionCompletionOutcome>>>(racers);

            // Racers 0 & 1: Resume / Recover
            for (var i = 0; i < 2; i++)
            {
                var store = stores[i];
                actions.Add(async () =>
                {
                    await using var repositories = store.CreateRepositories();
                    var recover = RecoveryHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => recover.HandleAsync(seeded.SessionId, ct));
                });
            }

            // Racers 2 & 3: Discard / Abandon
            for (var i = 2; i < 4; i++)
            {
                var store = stores[i];
                actions.Add(async () =>
                {
                    await using var repositories = store.CreateRepositories();
                    var discard = AbandonHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => discard.HandleAsync(seeded.SessionId, FocusEndReason.Unknown, ct));
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        AssertNoFailures(attempts);
        Assert.All(attempts, a => Assert.True(a.Value!.Success, a.Value.Error?.Message));

        // Exactly one racer changed the session state from RecoverableInterrupted to terminal
        var changingAttempts = attempts.Where(a => a.Value!.Changed).ToArray();
        Assert.Single(changingAttempts);

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        var session = await verification.FocusSessions.FindAsync(seeded.SessionId);
        Assert.NotNull(session);
        Assert.True(
            session!.State is SessionState.Completed or SessionState.Abandoned,
            $"Expected Completed or Abandoned terminal state, observed {session.State}.");

        // Exactly 0 active sessions remain
        Assert.Equal(0, await CountActiveSessionsAsync(verification, profileId));

        // Verify session events
        var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, seeded.SessionId);
        AssertUniqueEventSequences(events);
        Assert.Single(events, e => e.EventType is SessionEventType.Completed or SessionEventType.Abandoned);
        Assert.Single(events, e => e.EventType == SessionEventType.RewardCreated);

        // Verify ledger has strictly one reward entry for this session
        var ledger = await verification.RewardLedger.ListByProfileAsync(profileId);
        var sessionEntries = ledger
            .Where(e => string.Equals(e.SourceId, seeded.SessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var rewardEntry = Assert.Single(sessionEntries);
        Assert.Equal(
            session.State == SessionState.Completed
                ? LedgerSourceType.SessionCompletion
                : LedgerSourceType.SessionPartial,
            rewardEntry.SourceType);
        Assert.True(rewardEntry.Amount > 0);

        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task Concurrent_duplicate_reward_attempts_across_independent_stores_commit_strictly_once()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        const int racers = 8;
        var sourceId = Guid.NewGuid().ToString();

        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 10_000);
        Attempt<RewardOutcome>[] attempts;
        try
        {
            var actions = new List<Func<Task<RewardOutcome>>>(racers);
            foreach (var store in stores)
            {
                var currentStore = store;
                actions.Add(async () =>
                {
                    await using var repositories = currentStore.CreateRepositories();
                    var rewards = new RewardService(
                        repositories.RewardLedger,
                        repositories.Achievements,
                        repositories.UserAchievements,
                        repositories.RewardState,
                        db.Clock);

                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        async ct =>
                        {
                            var (entry, alreadyExisted) = await rewards.AwardAsync(
                                profileId,
                                CurrencyCode.Xp,
                                250,
                                LedgerSourceType.SessionCompletion,
                                sourceId,
                                notes: "Concurrent duplicate reward race test",
                                cancellationToken: ct);
                            return new RewardOutcome(entry!, alreadyExisted);
                        });
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        var failures = attempts.Where(a => a.Failure is not null).Select(a => a.Failure!.ToString()).ToArray();
        Assert.True(failures.Length == 0, "Exceptions thrown during concurrent reward attempts: " + string.Join(" | ", failures));
        Assert.All(attempts, a => Assert.NotNull(a.Value));

        // Every attempt obtained a valid entry with the identical Entry.Id
        var firstId = attempts[0].Value!.Entry.Id;
        Assert.All(attempts, a => Assert.Equal(firstId, a.Value!.Entry.Id));

        // Exactly one racer created the entry (alreadyExisted == false); 7 observed alreadyExisted == true
        Assert.Equal(1, attempts.Count(a => !a.Value!.AlreadyExisted));
        Assert.Equal(racers - 1, attempts.Count(a => a.Value!.AlreadyExisted));

        // Verify database: strictly one entry, correct XP sum
        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        var matchingEntries = (await verification.RewardLedger.ListByProfileAsync(profileId))
            .Where(e => e.SourceId == sourceId)
            .ToArray();
        var persisted = Assert.Single(matchingEntries);
        Assert.Equal(250, persisted.Amount);
        Assert.Equal(firstId, persisted.Id);

        var totalXp = await verification.RewardLedger.SumXpAsync(profileId);
        Assert.Equal(250, totalXp);

        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task Simultaneous_complete_session_race_awards_session_xp_and_achievements_strictly_once()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;
        var sessionId = await SeedActiveSessionAsync(db, profileId, plannedFocusSeconds: 600);

        const int racers = 4;
        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 10_000);
        Attempt<SessionCompletionOutcome>[] attempts;
        try
        {
            var actions = new List<Func<Task<SessionCompletionOutcome>>>(racers);
            foreach (var store in stores)
            {
                var currentStore = store;
                actions.Add(async () =>
                {
                    await using var repositories = currentStore.CreateRepositories();
                    var complete = CompleteHandler(repositories, db.Clock);
                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        ct => complete.HandleAsync(
                            sessionId,
                            elapsedSeconds: 600,
                            activeStudySeconds: 600,
                            idleSeconds: 0,
                            protectionGapSeconds: 0,
                            cancellationToken: ct));
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        AssertNoFailures(attempts);
        Assert.All(attempts, a => Assert.True(a.Value!.Success, a.Value.Error?.Message));

        // Exactly one racer transitioned the session and computed the reward transaction
        var changing = attempts.Where(a => a.Value!.Changed).ToArray();
        Assert.Single(changing);

        // All racers returned identical final TotalXp and Level
        var expectedTotalXp = changing[0].Value!.TotalXpAfter;
        Assert.All(attempts, a => Assert.Equal(expectedTotalXp, a.Value!.TotalXpAfter));

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();

        var session = await verification.FocusSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(SessionState.Completed, session!.State);
        Assert.Equal(0, await CountActiveSessionsAsync(verification, profileId));

        // Exactly 1 SessionCompletion reward in ledger for this session
        var sessionRewards = (await verification.RewardLedger.ListByProfileAsync(profileId))
            .Where(e => string.Equals(e.SourceId, sessionId.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var reward = Assert.Single(sessionRewards);
        Assert.Equal(LedgerSourceType.SessionCompletion, reward.SourceType);
        Assert.True(reward.Amount > 0);

        // ACH-001 (First Focus) unlocked exactly once
        var userAchievements = await verification.UserAchievements.ListByProfileAsync(profileId);
        var firstFocusUnlock = Assert.Single(userAchievements, a => a.AchievementCode == "ACH-001");
        Assert.NotNull(firstFocusUnlock.LedgerEntryId);

        var achievementRewards = (await verification.RewardLedger.ListByProfileAsync(profileId))
            .Where(e => e.SourceType == LedgerSourceType.AchievementUnlock && e.SourceId == "ACH-001")
            .ToArray();
        Assert.Single(achievementRewards);

        // Verification: Profile cached total matches ledger total
        var profile = await verification.LocalProfiles.FindAsync(profileId);
        Assert.NotNull(profile);
        var sumXp = await verification.RewardLedger.SumXpAsync(profileId);
        Assert.Equal(sumXp, profile!.CachedTotalXp);
        Assert.Equal(expectedTotalXp, profile.CachedTotalXp);

        // Verification: Session events
        var events = await verification.SessionEvents.ListBySessionAsync(SessionType.Focus, sessionId);
        AssertUniqueEventSequences(events);
        Assert.Single(events, e => e.EventType == SessionEventType.Completed);
        Assert.Single(events, e => e.EventType == SessionEventType.RewardCreated);
        Assert.Single(events, e => e.EventType == SessionEventType.AchievementUnlocked);

        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task Concurrent_achievement_evaluations_under_race_conditions_award_each_achievement_strictly_once()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        // Seed and complete a session directly to qualify for ACH-001 (First Focus),
        // but do not evaluate achievements during completion.
        var sessionId = await SeedActiveSessionAsync(db, profileId, plannedFocusSeconds: 600);
        await using (var repos = db.Store.CreateRepositories())
        {
            var session = await repos.FocusSessions.FindAsync(sessionId);
            session!.Complete(db.Clock.UtcNow, 600, 600, 0, 0);
            await repos.UnitOfWork.BeginAsync();
            await repos.SessionEvents.AddAsync(SessionEvent.Create(
                SessionType.Focus,
                sessionId,
                2,
                SessionEventType.Completed,
                db.Clock.UtcNow,
                db.Clock.ToLocalDate(db.Clock.UtcNow),
                db.Clock.UtcOffsetMinutes,
                "",
                db.Clock.UtcNow));
            await repos.Context.SaveChangesAsync();
            await repos.UnitOfWork.CommitAsync();
        }

        const int racers = 4;
        var stores = await CreateIndependentStoresAsync(db, racers, busyTimeoutMilliseconds: 10_000);
        Attempt<AchievementEvaluationOutcome>[] attempts;
        try
        {
            var actions = new List<Func<Task<AchievementEvaluationOutcome>>>(racers);
            foreach (var store in stores)
            {
                var currentStore = store;
                actions.Add(async () =>
                {
                    await using var repositories = currentStore.CreateRepositories();
                    var rewards = new RewardService(
                        repositories.RewardLedger,
                        repositories.Achievements,
                        repositories.UserAchievements,
                        repositories.RewardState,
                        db.Clock);

                    return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                        async ct =>
                        {
                            var codes = await rewards.EvaluateAndUnlockAchievementsAsync(profileId, ct);
                            return new AchievementEvaluationOutcome(codes);
                        });
                });
            }

            attempts = await RaceAttemptsAsync(actions);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }

        var failures = attempts.Where(a => a.Failure is not null).Select(a => a.Failure!.ToString()).ToArray();
        Assert.True(failures.Length == 0, "Exceptions during concurrent achievement evaluations: " + string.Join(" | ", failures));

        // Exactly one racer received the new unlock "ACH-001"; other racers received an empty list
        var allUnlocked = attempts.SelectMany(a => a.Value?.UnlockedCodes ?? Array.Empty<string>()).ToArray();
        var singleCode = Assert.Single(allUnlocked);
        Assert.Equal("ACH-001", singleCode);

        // Verification in database: exactly 1 UserAchievement and exactly 1 RewardLedgerEntry
        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();

        var userAchievements = await verification.UserAchievements.ListByProfileAsync(profileId);
        var achievement = Assert.Single(userAchievements);
        Assert.Equal("ACH-001", achievement.AchievementCode);

        var ledger = await verification.RewardLedger.ListByProfileAsync(profileId);
        var achievementLedger = Assert.Single(ledger, e => e.SourceType == LedgerSourceType.AchievementUnlock);
        Assert.Equal("ACH-001", achievementLedger.SourceId);
        Assert.Equal(50, achievementLedger.Amount);

        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task Interrupted_child_process_rolls_back_while_another_writer_is_actively_waiting()
    {
        await using var db = await TestDatabase.CreateAsync();
        await CreateProbeTableAsync(db);
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var options = db.Options with { BusyTimeoutMilliseconds = 15_000 };
        await using var store = new SqliteDataStore(options, db.Clock);
        await store.InitializeAsync();

        var readyFile = Path.Combine(db.Root, "child-ready.txt");
        var releaseFile = Path.Combine(db.Root, "child-release.txt");

        await using var child = await ChildWriterProcess.StartAsync(options.DatabasePath, readyFile, releaseFile);

        // Child process now holds the cross-process write gate mutex and has an uncommitted insert in SQLite.
        // Launch a concurrent write in the parent process. It will block on the write gate mutex.
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitingWriterTask = Task.Run(async () =>
        {
            writerStarted.SetResult();
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    return await CreateSubjectAsync(store, profileId, "committed-by-surviving-writer");
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode is 10 or 5)
                {
                    await Task.Delay(50);
                }
            }

            return await CreateSubjectAsync(store, profileId, "committed-by-surviving-writer");
        });

        // Ensure the waiting writer task has started and entered the write gate wait loop.
        await writerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        // Simulate crash/interruption of the active writer by killing the child process while the other writer is waiting.
        await child.KillAsync();

        // The waiting writer must catch AbandonedMutexException, SQLite rolls back the child's uncommitted write,
        // and the waiting writer completes its transaction successfully.
        var outcome = await waitingWriterTask.WaitAsync(OperationTimeout);
        Assert.True(outcome.Success, outcome.Error?.Message);

        // The killed child's probe row was completely rolled back.
        Assert.Equal(0, await db.ScalarAsync("SELECT COUNT(*) FROM \"__ConcurrencyProbe\";"));

        // The waiting writer's committed change is preserved.
        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        var subjects = await verification.Subjects.ListAsync(profileId, includeArchived: true);
        var committed = Assert.Single(subjects);
        Assert.Equal("committed-by-surviving-writer", committed.Name);

        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task Cross_process_session_start_times_out_while_child_holds_gate_then_succeeds_after_release()
    {
        await using var db = await TestDatabase.CreateAsync();
        await CreateProbeTableAsync(db);
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var options = db.Options with { BusyTimeoutMilliseconds = 500 };
        await using var store = new SqliteDataStore(options, db.Clock);
        await store.InitializeAsync();

        var readyFile = Path.Combine(db.Root, "child-ready.txt");
        var releaseFile = Path.Combine(db.Root, "child-release.txt");

        await using var child = await ChildWriterProcess.StartAsync(options.DatabasePath, readyFile, releaseFile);

        // Attempting to start a session times out because the child process holds the gate.
        await using (var repositories = store.CreateRepositories())
        {
            var start = StartHandler(repositories, db.Clock);
            var timeoutEx = await Assert.ThrowsAsync<WriteGateTimeoutException>(
                () => new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
                    ct => start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1_500, cancellationToken: ct)));
            Assert.Equal("WRITE_GATE_TIMEOUT", timeoutEx.Code);
        }

        // Child process releases and commits its uncommitted probe row.
        await child.ReleaseAndWaitAsync();
        Assert.Equal("child-uncommitted", await db.TextAsync("SELECT \"Value\" FROM \"__ConcurrencyProbe\" WHERE \"Id\" = 1;"));

        // Now session start succeeds immediately on the released database.
        await using (var retryRepositories = store.CreateRepositories())
        {
            var start = StartHandler(retryRepositories, db.Clock);
            var retryOutcome = await new CommandDispatcher(retryRepositories.UnitOfWork).ExecuteAsync(
                ct => start.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1_500, cancellationToken: ct));
            Assert.True(retryOutcome.Success, retryOutcome.Error?.Message);
        }

        await using var fresh = await OpenFreshStoreAsync(db);
        await using var verification = fresh.CreateRepositories();
        Assert.Equal(1, await CountActiveSessionsAsync(verification, profileId));
        await AssertDatabaseIntegrityAsync(db);
    }

    [Fact]
    public async Task In_process_transaction_rollback_while_another_writer_is_waiting_allows_waiting_writer_to_commit()
    {
        await using var db = await TestDatabase.CreateAsync();
        var profileId = db.Store.Bootstrap!.Seed.LocalProfileId;

        var stores = await CreateIndependentStoresAsync(db, 2, busyTimeoutMilliseconds: 10_000);
        try
        {
            var store1 = stores[0];
            var store2 = stores[1];

            // Writer 1 acquires the unit of work and write gate, then inserts uncommitted state.
            await using var repo1 = store1.CreateRepositories();
            await repo1.UnitOfWork.BeginAsync();

            var subject1 = Subject.Create(profileId, "uncommitted-writer-1", null, db.Clock.UtcNow);
            await repo1.Subjects.AddAsync(subject1);
            await repo1.Context.SaveChangesAsync();

            // Writer 2 starts in background, attempting to write on store2. It blocks on the write gate.
            var writer2Started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var writer2Task = Task.Run(async () =>
            {
                writer2Started.SetResult();
                return await CreateSubjectAsync(store2, profileId, "committed-writer-2");
            });

            await writer2Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(150);

            // Writer 1 rolls back while Writer 2 is actively waiting on the gate.
            await repo1.UnitOfWork.RollbackAsync();

            // Writer 2 unblocks and commits its work.
            var outcome2 = await writer2Task.WaitAsync(OperationTimeout);
            Assert.True(outcome2.Success, outcome2.Error?.Message);

            // Verification: Writer 1's entity was rolled back; Writer 2's entity was committed.
            await using var fresh = await OpenFreshStoreAsync(db);
            await using var verification = fresh.CreateRepositories();
            var subjects = await verification.Subjects.ListAsync(profileId, includeArchived: true);
            var committed = Assert.Single(subjects);
            Assert.Equal("committed-writer-2", committed.Name);
            Assert.DoesNotContain(subjects, s => s.Name == "uncommitted-writer-1");

            await AssertDatabaseIntegrityAsync(db);
        }
        finally
        {
            await DisposeStoresAsync(stores);
        }
    }

    // ------------------------------------------------------------------
    // Seeding and handler composition
    // ------------------------------------------------------------------

    private sealed record InterruptedSession(Guid SessionId, DateTime InterruptedAtUtc);

    private sealed record RewardOutcome(RewardLedgerEntry Entry, bool AlreadyExisted) : ICommandOutcome
    {
        public bool Success => true;
    }

    private sealed record AchievementEvaluationOutcome(IReadOnlyList<string> UnlockedCodes) : ICommandOutcome
    {
        public bool Success => true;
    }

    private static CompleteFocusSessionHandler CompleteHandler(SqliteRepositorySet repositories, ILocalClock clock) =>
        new(repositories.FocusSessions, clock, RecoveryHandler(repositories, clock));

    private static async Task<Guid> SeedActiveSessionAsync(
        TestDatabase db,
        Guid profileId,
        int plannedFocusSeconds)
    {
        await using var repositories = db.Store.CreateRepositories();
        var started = await StartHandler(repositories, db.Clock)
            .HandleAsync(profileId, BuiltInProfiles.BalancedCode, plannedFocusSeconds);
        Assert.True(started.Success, started.Error?.Message);
        return started.Id!.Value;
    }

    private static async Task AssertDatabaseIntegrityAsync(TestDatabase db)
    {
        Assert.Equal("ok", await db.TextAsync("PRAGMA integrity_check;"));
        Assert.Equal("ok", await db.TextAsync("PRAGMA quick_check;"));
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.False(await reader.ReadAsync(), "Foreign key violations detected.");
    }

    private static async Task<InterruptedSession> SeedInterruptedSessionAsync(
        TestDatabase db,
        Guid profileId,
        int plannedFocusSeconds,
        int elapsedSeconds,
        int activeStudySeconds)
    {
        await using var repositories = db.Store.CreateRepositories();
        var started = await StartHandler(repositories, db.Clock)
            .HandleAsync(profileId, BuiltInProfiles.BalancedCode, plannedFocusSeconds);
        Assert.True(started.Success, started.Error?.Message);
        var sessionId = started.Id!.Value;

        var checkpoint = new ApplyFocusCheckpointHandler(
            repositories.FocusSessions, repositories.UnitOfWork, db.Clock);
        var checkpointed = await checkpoint.HandleAsync(
            sessionId,
            elapsedSeconds,
            activeStudySeconds,
            Math.Max(0, elapsedSeconds - activeStudySeconds),
            0,
            0,
            0,
            0);
        Assert.True(checkpointed.Success, checkpointed.Error?.Message);

        db.Clock.UtcNow = db.Clock.UtcNow.AddMinutes(10);
        var interruptedAt = db.Clock.UtcNow;
        var interrupt = new MarkFocusInterruptedHandler(
            repositories.FocusSessions,
            repositories.UnitOfWork,
            db.Clock,
            new SessionEventRecorder(repositories.SessionEvents, db.Clock));
        var result = await interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash);
        Assert.True(result.Success, result.Error?.Message);

        return new InterruptedSession(sessionId, interruptedAt);
    }

    private static StartFocusSessionHandler StartHandler(SqliteRepositorySet repositories, ILocalClock clock) =>
        new(
            repositories.LocalProfiles,
            repositories.FocusProfiles,
            repositories.FocusSessions,
            repositories.BreakSessions,
            repositories.StudyTasks,
            repositories.Subjects,
            repositories.UnitOfWork,
            clock,
            new SessionEventRecorder(repositories.SessionEvents, clock),
            AbandonHandler(repositories, clock));

    private static AbandonFocusSessionHandler AbandonHandler(SqliteRepositorySet repositories, ILocalClock clock) =>
        new(repositories.FocusSessions, clock, RecoveryHandler(repositories, clock));

    private static RecoverFocusSessionHandler RecoveryHandler(
        SqliteRepositorySet repositories,
        ILocalClock clock) =>
        new(
            repositories.FocusSessions,
            repositories.LocalProfiles,
            repositories.UnitOfWork,
            new RewardService(
                repositories.RewardLedger,
                repositories.Achievements,
                repositories.UserAchievements,
                repositories.RewardState,
                clock),
            clock,
            new SessionEventRecorder(repositories.SessionEvents, clock));

    private static async Task<EntityResult> CreateSubjectAsync(
        SqliteDataStore store,
        Guid profileId,
        string name)
    {
        await using var repositories = store.CreateRepositories();
        var create = new CreateSubjectHandler(
            repositories.LocalProfiles, repositories.Subjects, repositories.UnitOfWork, store.Clock);
        return await new CommandDispatcher(repositories.UnitOfWork).ExecuteAsync(
            ct => create.HandleAsync(profileId, name, null, ct));
    }

    // ------------------------------------------------------------------
    // Race orchestration
    // ------------------------------------------------------------------

    private sealed record Attempt<T>(T? Value, Exception? Failure) where T : class;

    private static Task<Attempt<T>[]> RaceAttemptsAsync<T>(IReadOnlyList<Func<Task<T>>> actions)
        where T : class
    {
        var wrapped = new List<Func<Task<Attempt<T>>>>(actions.Count);
        foreach (var action in actions)
        {
            wrapped.Add(() => CaptureAsync(action));
        }

        return RaceAsync(wrapped);
    }

    private static async Task<Attempt<T>> CaptureAsync<T>(Func<Task<T>> action) where T : class
    {
        try
        {
            return new Attempt<T>(await action(), null);
        }
        catch (Exception ex)
        {
            return new Attempt<T>(null, ex);
        }
    }

    private static async Task<T> RunAfterAsync<T>(Task signal, Func<Task<T>> action)
    {
        await signal.WaitAsync(OperationTimeout);
        return await action().WaitAsync(OperationTimeout);
    }

    /// <summary>
    /// Releases every racer from one <see cref="TaskCompletionSource"/> and waits
    /// for all of them with a hard bound, so a deadlock fails instead of hanging.
    /// </summary>
    private static async Task<T[]> RaceAsync<T>(IReadOnlyList<Func<Task<T>>> actions)
    {
        using var timeout = new CancellationTokenSource(OperationTimeout);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task<T>[actions.Count];
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            tasks[i] = RunAsync(action);
        }

        gate.SetResult();
        return await Task.WhenAll(tasks).WaitAsync(OperationTimeout);

        async Task<T> RunAsync(Func<Task<T>> action)
        {
            await gate.Task.WaitAsync(timeout.Token);
            return await action().WaitAsync(timeout.Token);
        }
    }

    private static void AssertNoFailures<T>(IReadOnlyList<Attempt<T>> attempts)
        where T : class, ICommandOutcome
    {
        var failures = attempts
            .Where(a => a.Failure is not null)
            .Select(a => $"{a.Failure!.GetType().Name}: {a.Failure.Message}")
            .ToArray();
        Assert.True(failures.Length == 0, "Exceptions escaped a concurrent command: " + string.Join(" | ", failures));
        Assert.All(attempts, a => Assert.NotNull(a.Value));
    }

    private static string Describe<T>(IReadOnlyList<Attempt<T>> attempts)
        where T : class, ICommandOutcome =>
        string.Join(
            Environment.NewLine,
            attempts.Select((attempt, index) => attempt.Failure is not null
                ? $"[{index}] EX {attempt.Failure.GetType().Name}: {attempt.Failure.Message}"
                : $"[{index}] Success={attempt.Value!.Success}"));

    // ------------------------------------------------------------------
    // Store and database verification helpers
    // ------------------------------------------------------------------

    private static async Task<SqliteDataStore[]> CreateIndependentStoresAsync(
        TestDatabase db,
        int count,
        int busyTimeoutMilliseconds)
    {
        var options = db.Options with { BusyTimeoutMilliseconds = busyTimeoutMilliseconds };
        var stores = new SqliteDataStore[count];
        try
        {
            for (var i = 0; i < count; i++)
            {
                stores[i] = new SqliteDataStore(options, db.Clock);
                await stores[i].InitializeAsync();
            }

            return stores;
        }
        catch
        {
            await DisposeStoresAsync(stores);
            throw;
        }
    }

    private static async Task DisposeStoresAsync(IEnumerable<SqliteDataStore> stores)
    {
        foreach (var store in stores)
        {
            await store.DisposeAsync();
        }
    }

    private static async Task<SqliteDataStore> OpenFreshStoreAsync(TestDatabase db)
    {
        var store = new SqliteDataStore(db.Options, db.Clock);
        await store.InitializeAsync();
        return store;
    }

    private static Task<int> CountActiveSessionsAsync(SqliteRepositorySet repositories, Guid profileId) =>
        repositories.Context.FocusSessions.CountAsync(
            s => s.LocalProfileId == profileId && ActiveStates.Contains(s.State));

    private static void AssertUniqueEventSequences(IReadOnlyList<SessionEvent> events)
    {
        var duplicates = events
            .GroupBy(e => e.Sequence)
            .Where(group => group.Count() > 1)
            .Select(group => $"sequence {group.Key} appears {group.Count()} times")
            .ToArray();
        Assert.True(duplicates.Length == 0, "Duplicate session event sequences: " + string.Join(", ", duplicates));
        Assert.Equal(events.Count, events.Select(e => e.Sequence).Distinct().Count());
    }

    private static async Task CreateProbeTableAsync(TestDatabase db)
    {
        await using var connection = await db.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE IF NOT EXISTS \"__ConcurrencyProbe\" " +
            "(\"Id\" INTEGER NOT NULL PRIMARY KEY, \"Value\" TEXT NOT NULL);";
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Mirrors SqliteWriteGate.NormalizePath/BuildMutexName so the external child
    /// competes for the same named mutex as the production unit of work.
    /// </summary>
    private static string ComputeWriteGateMutexName(string databasePath)
    {
        var normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(databasePath))
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath));
        return PersistenceConstants.WriteGateMutexNamePrefix
            + Convert.ToHexString(hash.AsSpan(0, 16));
    }

    // ------------------------------------------------------------------
    // True multi-process child
    // ------------------------------------------------------------------

    /// <summary>
    /// A real second OS process. It acquires the production named write gate,
    /// starts a SQLite BEGIN IMMEDIATE transaction, inserts an uncommitted probe
    /// row, signals READY, then either commits after the release file appears or
    /// is killed by the test. It does not use any production assembly, so the
    /// test cannot accidentally collapse into a single-process simulation.
    /// </summary>
    private sealed class ChildWriterProcess : IAsyncDisposable
    {
        internal const string ChildSource = """
            using System;
            using System.IO;
            using System.Threading;
            using Microsoft.Data.Sqlite;

            internal static class ChildWriter
            {
                private static int Main(string[] args)
                {
                    if (args.Length < 4)
                    {
                        Console.Error.WriteLine("usage: <db> <mutex> <ready> <release>");
                        return 64;
                    }

                    var databasePath = args[0];
                    var mutexName = args[1];
                    var readyFile = args[2];
                    var releaseFile = args[3];

                    SQLitePCL.Batteries_V2.Init();

                    using var mutex = new Mutex(false, mutexName);
                    var ownsMutex = false;
                    try
                    {
                        try
                        {
                            ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(10));
                        }
                        catch (AbandonedMutexException)
                        {
                            ownsMutex = true;
                        }

                        if (!ownsMutex)
                        {
                            Console.Error.WriteLine("write-gate-timeout");
                            return 3;
                        }

                        var connectionString = new SqliteConnectionStringBuilder
                        {
                            DataSource = databasePath,
                            Mode = SqliteOpenMode.ReadWrite,
                            Pooling = false,
                            ForeignKeys = true,
                            DefaultTimeout = 10,
                        }.ToString();

                        using var connection = new SqliteConnection(connectionString);
                        connection.Open();
                        using (var pragma = connection.CreateCommand())
                        {
                            pragma.CommandText = "PRAGMA busy_timeout = 10000; PRAGMA synchronous = NORMAL;";
                            pragma.ExecuteNonQuery();
                        }

                        using var transaction = connection.BeginTransaction(deferred: false);
                        using (var insert = connection.CreateCommand())
                        {
                            insert.Transaction = transaction;
                            insert.CommandText =
                                "INSERT INTO \"__ConcurrencyProbe\" (\"Id\", \"Value\") " +
                                "VALUES (1, 'child-uncommitted');";
                            insert.ExecuteNonQuery();
                        }

                        File.WriteAllText(readyFile, "ready");

                        var deadline = DateTime.UtcNow.AddSeconds(60);
                        while (!File.Exists(releaseFile))
                        {
                            if (DateTime.UtcNow > deadline)
                            {
                                Console.Error.WriteLine("release-timeout");
                                return 4;
                            }

                            Thread.Sleep(20);
                        }

                        transaction.Commit();
                        return 0;
                    }
                    finally
                    {
                        if (ownsMutex)
                        {
                            try
                            {
                                mutex.ReleaseMutex();
                            }
                            catch (ApplicationException)
                            {
                            }
                        }
                    }
                }
            }
            """;

        private readonly Process _process;
        private readonly string _releaseFile;

        private ChildWriterProcess(Process process, string releaseFile)
        {
            _process = process;
            _releaseFile = releaseFile;
        }

        public static async Task<ChildWriterProcess> StartAsync(
            string databasePath,
            string readyFile,
            string releaseFile)
        {
            var helperAssembly = await ChildWriterCompiler.GetAssemblyPathAsync();
            var startInfo = new ProcessStartInfo
            {
                FileName = ChildWriterCompiler.DotnetPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(helperAssembly)!,
            };
            startInfo.ArgumentList.Add(helperAssembly);
            startInfo.ArgumentList.Add(databasePath);
            startInfo.ArgumentList.Add(ComputeWriteGateMutexName(databasePath));
            startInfo.ArgumentList.Add(readyFile);
            startInfo.ArgumentList.Add(releaseFile);

            var process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The child writer process could not be started.");
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(readyFile))
            {
                if (process.HasExited)
                {
                    var exitCode = process.ExitCode;
                    var error = await process.StandardError.ReadToEndAsync();
                    process.Dispose();
                    throw new InvalidOperationException(
                        $"The child writer exited before READY with code {exitCode}: {error}");
                }

                if (deadline.Elapsed > TimeSpan.FromSeconds(20))
                {
                    process.Kill(entireProcessTree: true);
                    process.Dispose();
                    throw new TimeoutException("The child writer did not signal READY within 20 seconds.");
                }

                await Task.Delay(25);
            }

            return new ChildWriterProcess(process, releaseFile);
        }

        public async Task ReleaseAndWaitAsync()
        {
            await File.WriteAllTextAsync(_releaseFile, "release");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _process.WaitForExitAsync(timeout.Token);
            if (_process.ExitCode != 0)
            {
                var error = await _process.StandardError.ReadToEndAsync();
                throw new InvalidOperationException(
                    $"The child writer exited with code {_process.ExitCode}: {error}");
            }
        }

        public async Task KillAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await _process.WaitForExitAsync(timeout.Token);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await _process.WaitForExitAsync(timeout.Token);
                }
            }
            finally
            {
                _process.Dispose();
            }
        }
    }

    /// <summary>
    /// Compiles the child once per test process with the SDK's csc, referencing
    /// the same Microsoft.Data.Sqlite/SQLitePCLRaw assemblies the test uses. This
    /// avoids a separate project and keeps the helper disposable.
    /// </summary>
    private static class ChildWriterCompiler
    {
        private static readonly Lazy<Task<string>> Assembly = new(
            BuildAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);

        public static string DotnetPath { get; } = ResolveDotnetPath();

        public static Task<string> GetAssemblyPathAsync() => Assembly.Value;

        private static string ResolveDotnetPath()
        {
            var coreLibrary = typeof(object).Assembly.Location;
            var dotnetRoot = Path.GetFullPath(
                Path.Combine(Path.GetDirectoryName(coreLibrary)!, "..", "..", ".."));
            var candidate = Path.Combine(dotnetRoot, "dotnet.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            throw new InvalidOperationException($"Could not locate dotnet.exe under '{dotnetRoot}'.");
        }

        private static async Task<string> BuildAsync()
        {
            var dotnetRoot = Path.GetDirectoryName(DotnetPath)!;
            var sdkDirectory = Directory.GetDirectories(Path.Combine(dotnetRoot, "sdk"))
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .First();
            var cscPath = Path.Combine(sdkDirectory, "Roslyn", "bincore", "csc.dll");
            var referenceRoot = Path.Combine(dotnetRoot, "packs", "Microsoft.NETCore.App.Ref");
            var referenceVersion = Directory.GetDirectories(referenceRoot)
                .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .First();
            var referenceDirectory = Path.Combine(referenceVersion, "ref", "net10.0");
            var runtimeDirectory = Path.GetDirectoryName(typeof(SqliteConnection).Assembly.Location)!;

            var outputDirectory = Path.Combine(
                Path.GetTempPath(),
                "BoundlessStudy.Infrastructure.Tests",
                "mp-helper-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(outputDirectory);

            var sourcePath = Path.Combine(outputDirectory, "ChildWriter.cs");
            await File.WriteAllTextAsync(sourcePath, ChildWriterProcess.ChildSource);

            var references = new List<string>();
            foreach (var reference in Directory.GetFiles(referenceDirectory, "*.dll"))
            {
                references.Add($"-r:\"{reference}\"");
            }

            foreach (var dependency in new[]
                     {
                         "Microsoft.Data.Sqlite.dll",
                         "SQLitePCLRaw.core.dll",
                         "SQLitePCLRaw.provider.e_sqlite3.dll",
                         "SQLitePCLRaw.batteries_v2.dll",
                     })
            {
                references.Add($"-r:\"{Path.Combine(runtimeDirectory, dependency)}\"");
            }

            var responseFile = Path.Combine(outputDirectory, "references.rsp");
            await File.WriteAllLinesAsync(responseFile, references);

            var assemblyPath = Path.Combine(outputDirectory, "ChildWriter.dll");
            var compilerInfo = new ProcessStartInfo
            {
                FileName = DotnetPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = outputDirectory,
            };
            compilerInfo.ArgumentList.Add(cscPath);
            compilerInfo.ArgumentList.Add("-nologo");
            compilerInfo.ArgumentList.Add("-langversion:latest");
            compilerInfo.ArgumentList.Add("-nullable:enable");
            compilerInfo.ArgumentList.Add("-target:exe");
            compilerInfo.ArgumentList.Add($"-out:{assemblyPath}");
            compilerInfo.ArgumentList.Add($"@{responseFile}");
            compilerInfo.ArgumentList.Add(sourcePath);

            using var compiler = Process.Start(compilerInfo)
                ?? throw new InvalidOperationException("The C# compiler process could not be started.");
            var standardError = await compiler.StandardError.ReadToEndAsync();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                await compiler.WaitForExitAsync(timeout.Token);
            }

            if (compiler.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"The child writer helper did not compile (exit {compiler.ExitCode}):{Environment.NewLine}{standardError}");
            }

            foreach (var dependency in new[]
                     {
                         "Microsoft.Data.Sqlite.dll",
                         "SQLitePCLRaw.core.dll",
                         "SQLitePCLRaw.provider.e_sqlite3.dll",
                         "SQLitePCLRaw.batteries_v2.dll",
                     })
            {
                File.Copy(Path.Combine(runtimeDirectory, dependency), Path.Combine(outputDirectory, dependency), true);
            }

            var nativePath = Path.Combine(runtimeDirectory, "runtimes", "win-x64", "native", "e_sqlite3.dll");
            if (!File.Exists(nativePath))
            {
                nativePath = Path.Combine(runtimeDirectory, "e_sqlite3.dll");
            }

            File.Copy(nativePath, Path.Combine(outputDirectory, "e_sqlite3.dll"), true);

            await File.WriteAllTextAsync(
                Path.Combine(outputDirectory, "ChildWriter.runtimeconfig.json"),
                """
                {
                  "runtimeOptions": {
                    "tfm": "net10.0",
                    "framework": {
                      "name": "Microsoft.NETCore.App",
                      "version": "10.0.0"
                    },
                    "rollForward": "LatestPatch"
                  }
                }
                """);

            return assemblyPath;
        }
    }

    // ------------------------------------------------------------------
    // Injected failure
    // ------------------------------------------------------------------

    /// <summary>Delegates every read but throws after the session event was saved.</summary>
    private sealed class ThrowingRewardLedgerRepository : IRewardLedgerRepository
    {
        private readonly IRewardLedgerRepository _inner;

        public ThrowingRewardLedgerRepository(IRewardLedgerRepository inner)
        {
            _inner = inner;
        }

        public Task<RewardLedgerEntry?> FindByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken cancellationToken = default) =>
            _inner.FindByIdempotencyKeyAsync(idempotencyKey, cancellationToken);

        public Task<int> SumSessionXpForLocalDateAsync(
            Guid localProfileId,
            DateOnly awardLocalDate,
            CancellationToken cancellationToken = default) =>
            _inner.SumSessionXpForLocalDateAsync(localProfileId, awardLocalDate, cancellationToken);

        public Task<long> SumXpAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
            _inner.SumXpAsync(localProfileId, cancellationToken);

        public Task<IReadOnlyList<RewardLedgerEntry>> ListByProfileAsync(
            Guid localProfileId,
            CancellationToken cancellationToken = default) =>
            _inner.ListByProfileAsync(localProfileId, cancellationToken);

        public Task AddAsync(RewardLedgerEntry entry, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("simulated ledger failure after the session transition");
    }
}
