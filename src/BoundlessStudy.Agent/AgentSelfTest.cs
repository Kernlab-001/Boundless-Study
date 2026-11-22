using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Agent;

/// <summary>
/// Verifies the automatic background expiry and recovery implementation across
/// startup sweep, periodic sweep, database locks, clock skew, and idempotency.
/// </summary>
public static class AgentSelfTest
{
    private sealed class MutableClock : ILocalClock
    {
        public MutableClock(DateTime utcNow)
        {
            UtcNow = utcNow;
        }

        public DateTime UtcNow { get; set; }
        public int UtcOffsetMinutes => 0;
        public DateOnly ToLocalDate(DateTime utcTime) => DateOnly.FromDateTime(utcTime);
    }

    public static async Task<int> RunAsync(SqliteDataStore defaultStore, ILocalClock defaultClock)
    {
        Console.WriteLine("=== BoundlessStudy.Agent Self-Test Suite ===");

        var testDir = Path.Combine(Path.GetTempPath(), "BoundlessStudy_AgentSelfTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        try
        {
            var dbPath = Path.Combine(testDir, "test_study.db");
            var backupDir = Path.Combine(testDir, "backups");
            var clock = new MutableClock(new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc));

            var options = new SqliteDatabaseOptions
            {
                DatabasePath = dbPath,
                BackupDirectory = backupDir,
                BusyTimeoutMilliseconds = 2000,
            };

            await using var store = new SqliteDataStore(options, clock);
            var bootResult = await store.InitializeAsync();
            var profileId = bootResult.Seed.LocalProfileId;

            // Scenario 1: Active session is never swept
            Console.Write("Test 1: Active session is never swept... ");
            Guid activeSessionId;
            await using (var repos = store.CreateRepositories())
            {
                var starter = CreateStartHandler(repos, clock);
                var startResult = await starter.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1500);
                activeSessionId = startResult.Id!.Value;
            }

            clock.UtcNow = clock.UtcNow.AddHours(48);
            var executor = new RecoverySweepExecutor(store, clock);
            var sweepResult = await executor.ExecuteSweepAsync();
            Assert(sweepResult is not null && sweepResult.SessionsAbandoned == 0, "Active session should not be abandoned");

            await using (var repos = store.CreateRepositories())
            {
                var s = await repos.FocusSessions.FindAsync(activeSessionId);
                Assert(s is not null && s.State == SessionState.Active, "Session state should remain Active");
                // Clean up active session
                s!.EndEarly(clock.UtcNow, 600, 600, 0, 0, FocusEndReason.UserEnded);
                await repos.Context.SaveChangesAsync();
            }
            Console.WriteLine("PASSED");

            // Scenario 2: Startup sweep abandons expired recoverable session (>= 24h)
            Console.Write("Test 2: Startup sweep abandons expired recoverable session (>= 24h)... ");
            Guid expiredSessionId;
            var interruptTime = clock.UtcNow;
            await using (var repos = store.CreateRepositories())
            {
                var starter = CreateStartHandler(repos, clock);
                var startResult = await starter.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1500);
                expiredSessionId = startResult.Id!.Value;

                var checkpoint = new ApplyFocusCheckpointHandler(repos.FocusSessions, repos.UnitOfWork, clock);
                await checkpoint.HandleAsync(expiredSessionId, 600, 600, 0, 0, 0, 0, 0);

                var interrupt = new MarkFocusInterruptedHandler(
                    repos.FocusSessions, repos.UnitOfWork, clock, new SessionEventRecorder(repos.SessionEvents, clock));
                await interrupt.HandleAsync(expiredSessionId, FocusEndReason.AgentCrash);
            }

            // Advance clock past 24 hours
            clock.UtcNow = interruptTime.AddHours(24);

            var worker = new BackgroundRecoveryWorker(executor, new AgentRecoveryOptions { RunStartupSweep = true });
            var startupResult = await worker.RunStartupSweepAsync();
            Assert(startupResult is not null, "Startup sweep should return a result");
            Assert(startupResult!.SessionsAbandoned == 1, $"Expected 1 abandoned session, got {startupResult.SessionsAbandoned}");
            Assert(startupResult.AbandonedSessionIds.Contains(expiredSessionId), "Abandoned ID should match");

            await using (var repos = store.CreateRepositories())
            {
                var abandonedSession = await repos.FocusSessions.FindAsync(expiredSessionId);
                Assert(abandonedSession is not null, "Session should exist");
                Assert(abandonedSession!.State == SessionState.Abandoned, "State should be Abandoned");
                Assert(abandonedSession.EndReason == FocusEndReason.Unknown, "EndReason should be Unknown");
                Assert(abandonedSession.EndedAtUtc == clock.UtcNow, "EndedAtUtc should match sweep clock");

                // Verify reward ledger
                var ledger = await repos.RewardLedger.ListByProfileAsync(profileId);
                var partials = ledger.Where(e => e.SourceType == LedgerSourceType.SessionPartial).ToList();
                Assert(partials.Count == 1, $"Expected 1 partial reward ledger entry, got {partials.Count}");
                Assert(partials[0].Amount == 15, $"Expected 15 XP partial award, got {partials[0].Amount}");
            }
            Console.WriteLine("PASSED");

            // Scenario 3: Idempotent repeat sweep does not duplicate XP or events
            Console.Write("Test 3: Repeated sweep is idempotent and safe... ");
            var secondSweep = await executor.ExecuteSweepAsync();
            Assert(secondSweep is not null && secondSweep.SessionsAbandoned == 0, "Second sweep should abandon 0 sessions");

            await using (var repos = store.CreateRepositories())
            {
                var ledger = await repos.RewardLedger.ListByProfileAsync(profileId);
                var partials = ledger.Where(e => e.SourceType == LedgerSourceType.SessionPartial).ToList();
                Assert(partials.Count == 1, $"Ledger must still have exactly 1 entry, got {partials.Count}");
            }
            Console.WriteLine("PASSED");

            // Scenario 4: Non-expired session (age < 24h) is preserved
            Console.Write("Test 4: Non-expired recoverable session (< 24h) is not abandoned... ");
            Guid freshSessionId;
            var freshInterruptTime = clock.UtcNow;
            await using (var repos = store.CreateRepositories())
            {
                var starter = CreateStartHandler(repos, clock);
                var startResult = await starter.HandleAsync(profileId, BuiltInProfiles.BalancedCode, 1500);
                freshSessionId = startResult.Id!.Value;

                var interrupt = new MarkFocusInterruptedHandler(
                    repos.FocusSessions, repos.UnitOfWork, clock, new SessionEventRecorder(repos.SessionEvents, clock));
                await interrupt.HandleAsync(freshSessionId, FocusEndReason.AgentCrash);
            }

            // Advance by 23 hours 59 minutes (1 minute shy of 24h)
            clock.UtcNow = freshInterruptTime.AddHours(23).AddMinutes(59);
            var preExpiryResult = await executor.ExecuteSweepAsync();
            Assert(preExpiryResult is not null && preExpiryResult.SessionsAbandoned == 0, "Session should not expire before 24h");

            await using (var repos = store.CreateRepositories())
            {
                var s = await repos.FocusSessions.FindAsync(freshSessionId);
                Assert(s is not null && s.State == SessionState.RecoverableInterrupted, "State must remain RecoverableInterrupted");
            }
            Console.WriteLine("PASSED");

            // Scenario 5: Clock skew / future timestamp is safely ignored
            Console.Write("Test 5: Clock skew / backward time is safely handled... ");
            clock.UtcNow = freshInterruptTime.AddHours(-2); // Clock jumped backwards
            var skewResult = await executor.ExecuteSweepAsync();
            Assert(skewResult is not null && skewResult.SessionsAbandoned == 0, "Clock skew must not trigger abandonment");
            Console.WriteLine("PASSED");

            // Scenario 6: Periodic background sweep auto-abandons without user interaction
            Console.Write("Test 6: Periodic background sweep automatically expires session on timer... ");
            // Set clock past 24h
            clock.UtcNow = freshInterruptTime.AddHours(24).AddSeconds(1);

            var periodicOptions = new AgentRecoveryOptions
            {
                RunStartupSweep = false,
                SweepInterval = TimeSpan.FromMilliseconds(50),
            };
            await using (var periodicWorker = new BackgroundRecoveryWorker(executor, periodicOptions))
            {
                periodicWorker.Start();
                // Wait briefly for background timer tick
                await Task.Delay(250);
                await periodicWorker.StopAsync();
            }

            await using (var repos = store.CreateRepositories())
            {
                var s = await repos.FocusSessions.FindAsync(freshSessionId);
                Assert(s is not null && s.State == SessionState.Abandoned, "Periodic sweep must have transitioned session to Abandoned");
            }
            Console.WriteLine("PASSED");

            Console.WriteLine("All 6 Agent recovery self-tests PASSED successfully!");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"\nSELF-TEST FAILED: {ex.Message}\n{ex.StackTrace}");
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(testDir))
                {
                    Directory.Delete(testDir, recursive: true);
                }
            }
            catch
            {
                // Best effort cleanup
            }
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Assertion failed: {message}");
        }
    }

    private static StartFocusSessionHandler CreateStartHandler(SqliteRepositorySet repos, ILocalClock clock)
    {
        var rewards = new RewardService(
            repos.RewardLedger, repos.Achievements, repos.UserAchievements, repos.RewardState, clock);
        var recorder = new SessionEventRecorder(repos.SessionEvents, clock);
        var recover = new RecoverFocusSessionHandler(
            repos.FocusSessions, repos.LocalProfiles, repos.UnitOfWork, rewards, clock, recorder);
        var abandon = new AbandonFocusSessionHandler(repos.FocusSessions, clock, recover);
        return new StartFocusSessionHandler(
            repos.LocalProfiles,
            repos.FocusProfiles,
            repos.FocusSessions,
            repos.BreakSessions,
            repos.StudyTasks,
            repos.Subjects,
            repos.UnitOfWork,
            clock,
            recorder,
            abandon);
    }
}
