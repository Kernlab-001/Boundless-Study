using System.Diagnostics;
using Boundless.Protection.Engine;
using Xunit;

namespace Boundless.Protection.Engine.Tests;

public sealed class ProcessCrashRecoveryTests
{
    private const string Subject = "S-1-5-21-111111111-222222222-333333333-1001";
    private static readonly Guid RequestId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid SessionId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid FirstOwnedRule = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");
    private static readonly Guid SecondOwnedRule = Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd");

    [Fact]
    public async Task AbruptChildCrashAfterPartialCommit_RecoveryUsesNewSqliteObjectsAndPreservesExternalRule()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journalPath = Path.Combine(directory, "journal.db");
            var providerPath = Path.Combine(directory, "synthetic-policy.db");
            var external = new ProtectionRule(Guid.NewGuid(), Guid.NewGuid(), Subject,
                new ApplicationIdentity("C:\\BPE-Synthetic\\unrelated-admin-rule.exe", new string('e', 64)));

            // Initialize isolated disposable stores and a baseline external rule.
            _ = new SqliteProtectionJournal(journalPath);
            new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker).AddExternalRule(external);

            var workerDll = FindWorkerDll();
            Assert.True(File.Exists(workerDll), $"Crash worker was not built: {workerDll}. Build the solution first.");
            await RunCrashWorkerAsync(workerDll, "crash-after-partial-apply", journalPath, providerPath);

            // New instances over only the persisted database files represent a new
            // process opening durable state after the child was terminated.
            var recoveredJournal = new SqliteProtectionJournal(journalPath);
            var recoveredProvider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            var before = Assert.Single(recoveredJournal.All());
            Assert.Equal(ProtectionState.Applying, before.State);
            Assert.Contains((await recoveredProvider.SnapshotAsync()).Rules, r => r.RuleId == FirstOwnedRule);

            var engine = new ProtectionEngine([recoveredProvider], recoveredJournal, new InMemoryProtectionAuditSink());
            var recoveryResults = await engine.RecoverAsync();

            Assert.Single(recoveryResults);
            Assert.Equal(ProtectionOutcome.Restored, recoveryResults[0].Outcome);
            var remaining = (await recoveredProvider.SnapshotAsync()).Rules;
            Assert.Contains(remaining, r => r.RuleId == external.RuleId && r == external);
            Assert.DoesNotContain(remaining, r => r.RuleId == FirstOwnedRule || r.RuleId == SecondOwnedRule);
            Assert.Equal(ProtectionState.Restored, recoveredJournal.Find(RequestId)!.State);

            // Reopening again and repeating recovery is idempotent.
            var reopenedJournal = new SqliteProtectionJournal(journalPath);
            var reopenedProvider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            var repeated = await new ProtectionEngine([reopenedProvider], reopenedJournal, new InMemoryProtectionAuditSink()).RecoverAsync();
            Assert.Empty(repeated);
        }
        finally
        {
            SqliteConnectionCleanup();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CrashAfterProviderRestoreCommit_RecoveryCompletesJournalUpdate()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Restore-Crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journalPath = Path.Combine(directory, "journal.db");
            var providerPath = Path.Combine(directory, "synthetic-policy.db");
            var journal = new SqliteProtectionJournal(journalPath);
            var provider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            var request = CreateRequest();
            var engine = new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink());
            Assert.Equal(ProtectionOutcome.Applied, (await engine.ApplyAsync(request)).Outcome);

            await RunCrashWorkerAsync(FindWorkerDll(), "crash-after-restore", journalPath, providerPath);

            var reopenedJournal = new SqliteProtectionJournal(journalPath);
            var reopenedProvider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            Assert.Equal(ProtectionState.Restoring, reopenedJournal.Find(RequestId)!.State);
            Assert.Empty((await reopenedProvider.SnapshotAsync()).Rules);

            var freshEngine = new ProtectionEngine([reopenedProvider], reopenedJournal, new InMemoryProtectionAuditSink());
            var recovery = await freshEngine.RecoverAsync();
            Assert.Single(recovery);
            Assert.Equal(ProtectionOutcome.NoOp, recovery[0].Outcome);
            Assert.Equal(ProtectionState.Restored, reopenedJournal.Find(RequestId)!.State);
        }
        finally
        {
            SqliteConnectionCleanup();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("crash-before-apply", 0)]
    [InlineData("crash-after-complete-apply", 2)]
    public async Task CrashAtApplyBoundary_RecoveryRestoresDurableJournal(string mode, int expectedOwnedRules)
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journalPath = Path.Combine(directory, "journal.db");
            var providerPath = Path.Combine(directory, "synthetic-policy.db");
            _ = new SqliteProtectionJournal(journalPath);
            var provider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            await RunCrashWorkerAsync(FindWorkerDll(), mode, journalPath, providerPath);

            var journal = new SqliteProtectionJournal(journalPath);
            var reopenedProvider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            Assert.Equal(expectedOwnedRules, (await reopenedProvider.GetOwnedRulesAsync(RequestId)).Count);
            Assert.Equal(ProtectionState.Applying, journal.Find(RequestId)!.State);
            await new ProtectionEngine([reopenedProvider], journal, new InMemoryProtectionAuditSink()).RecoverAsync();
            Assert.Empty((await reopenedProvider.SnapshotAsync()).Rules);
            Assert.Equal(ProtectionState.Restored, journal.Find(RequestId)!.State);
        }
        finally
        {
            SqliteConnectionCleanup();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CrashDuringRestoreMutation_RecoveryRemovesRemainingOwnedRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-During-Restore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journalPath = Path.Combine(directory, "journal.db");
            var providerPath = Path.Combine(directory, "synthetic-policy.db");
            var journal = new SqliteProtectionJournal(journalPath);
            var provider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            Assert.Equal(ProtectionOutcome.Applied, (await new ProtectionEngine([provider], journal, new InMemoryProtectionAuditSink()).ApplyAsync(CreateRequest())).Outcome);

            await RunCrashWorkerAsync(FindWorkerDll(), "crash-during-restore", journalPath, providerPath);
            var freshJournal = new SqliteProtectionJournal(journalPath);
            var freshProvider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            Assert.Equal(ProtectionState.Restoring, freshJournal.Find(RequestId)!.State);
            Assert.Single((await freshProvider.SnapshotAsync()).Rules);

            await new ProtectionEngine([freshProvider], freshJournal, new InMemoryProtectionAuditSink()).RecoverAsync();
            Assert.Empty((await freshProvider.SnapshotAsync()).Rules);
            Assert.Equal(ProtectionState.Restored, freshJournal.Find(RequestId)!.State);
        }
        finally
        {
            SqliteConnectionCleanup();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task SeparateProcesses_DuplicateRequestIsSerializedAndLockIsReleasedOnTermination()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Boundless-BPE-Lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var journalPath = Path.Combine(directory, "journal.db");
            var providerPath = Path.Combine(directory, "synthetic-policy.db");
            _ = new SqliteProtectionJournal(journalPath);
            _ = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            var workerDll = FindWorkerDll();
            using var first = StartWorker(workerDll, "apply", journalPath, providerPath);
            using var second = StartWorker(workerDll, "apply", journalPath, providerPath);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await Task.WhenAll(first.WaitForExitAsync(timeout.Token), second.WaitForExitAsync(timeout.Token));
            Assert.Equal(0, first.ExitCode);
            Assert.Equal(0, second.ExitCode);
            var journal = new SqliteProtectionJournal(journalPath);
            var provider = new PersistentSimulatedPolicyProvider(providerPath, ProtectionProviderKind.AppLocker);
            Assert.Single(journal.All());
            Assert.Equal(2, (await provider.SnapshotAsync()).Rules.Count);

            var ready = Path.Combine(directory, "lock-ready.txt");
            using var holder = StartWorker(workerDll, "hold-lock", journalPath, providerPath, ready);
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!File.Exists(ready) && DateTime.UtcNow < deadline) await Task.Delay(100);
            Assert.True(File.Exists(ready), "Child did not signal that the lock was held.");
            Assert.Throws<IOException>(() => journal.AcquireOperationLock());

            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync();
            using (journal.AcquireOperationLock()) { }
        }
        finally
        {
            SqliteConnectionCleanup();
            try { Directory.Delete(directory, recursive: true); } catch (IOException) { }
        }
    }

    private static ProtectionRequest CreateRequest() => new(
        RequestId,
        SessionId,
        1,
        Subject,
        ProtectionProviderKind.AppLocker,
        [
            new RequestedRule(FirstOwnedRule, Subject,
                new ApplicationIdentity("C:\\BPE-Synthetic\\child-one.exe", new string('a', 64))),
            new RequestedRule(SecondOwnedRule, Subject,
                new ApplicationIdentity("C:\\BPE-Synthetic\\child-two.exe", new string('b', 64)))
        ]);

    private static async Task RunCrashWorkerAsync(string workerDll, string mode, string journalPath, string providerPath)
    {
        Assert.True(File.Exists(workerDll), $"Crash worker was not built: {workerDll}. Build the solution first.");
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(workerDll);
        process.StartInfo.ArgumentList.Add(mode);
        process.StartInfo.ArgumentList.Add(journalPath);
        process.StartInfo.ArgumentList.Add(providerPath);
        Assert.True(process.Start(), "Could not start the BPE crash worker.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"Crash worker '{mode}' exceeded its 30 second bound.");
        }
        var standardOutput = await stdout;
        var standardError = await stderr;
        Assert.NotEqual(0, process.ExitCode);
        Assert.True((standardError + standardOutput).Contains("Intentional BPE synthetic crash", StringComparison.OrdinalIgnoreCase),
            $"Worker exited before the intended crash. stdout: {standardOutput} stderr: {standardError}");
    }

    private static Process StartWorker(string workerDll, string mode, string journalPath, string providerPath, string? extraArgument = null)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.StartInfo.ArgumentList.Add(workerDll);
        process.StartInfo.ArgumentList.Add(mode);
        process.StartInfo.ArgumentList.Add(journalPath);
        process.StartInfo.ArgumentList.Add(providerPath);
        if (extraArgument is not null) process.StartInfo.ArgumentList.Add(extraArgument);
        if (!process.Start()) throw new InvalidOperationException("Could not start the BPE worker process.");
        _ = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        return process;
    }

    private static string FindWorkerDll()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "BoundlessStudy.slnx")))
            directory = directory.Parent;
        if (directory is null) throw new DirectoryNotFoundException("Could not locate the solution root from the test output directory.");
        return Path.Combine(directory.FullName, "prototypes", "Boundless.Protection.Engine.CrashWorker", "bin", "Release", "net10.0", "Boundless.Protection.Engine.CrashWorker.dll");
    }

    private static void SqliteConnectionCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}
