using BoundlessStudy.Agent;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Infrastructure.Persistence;
using System.Security.Principal;

// Per-user background agent: the only process that opens SQLite (ADR-0005).
// Initializes database, runs migrations and schema handshake, seeds reference data,
// runs startup recovery sweep for expired sessions, runs periodic background recovery sweep,
// and hosts the Named Pipes IPC server for the Desktop UI.

if (args.Any(a => a is "--self-test"))
{
    var dummyClock = new SystemLocalClock();
    return await AgentSelfTest.RunAsync(null!, dummyClock);
}

var defaultOptions = SqliteDatabaseOptions.ForCurrentUser();
var databasePath = defaultOptions.DatabasePath;
var backupDirectory = defaultOptions.BackupDirectory;
var hasExplicitDatabasePath = false;
var hasExplicitBackupDirectory = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] is "--database-path" && i + 1 < args.Length)
    {
        databasePath = Path.GetFullPath(args[++i]);
        hasExplicitDatabasePath = true;
    }
    else if (args[i] is "--backup-directory" && i + 1 < args.Length)
    {
        backupDirectory = Path.GetFullPath(args[++i]);
        hasExplicitBackupDirectory = true;
    }
}
if (hasExplicitDatabasePath && string.Equals(databasePath, Path.GetFullPath(defaultOptions.DatabasePath), StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("An explicit test database path must not resolve to the production database.");
}
if (hasExplicitDatabasePath && !hasExplicitBackupDirectory)
{
    throw new InvalidOperationException("An explicit test database path requires an explicit disposable backup directory.");
}
if (hasExplicitBackupDirectory && string.Equals(backupDirectory, Path.GetFullPath(defaultOptions.BackupDirectory!), StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException("An explicit test backup directory must not resolve to the production backup directory.");
}
var options = defaultOptions with { DatabasePath = databasePath, BackupDirectory = backupDirectory };
var clock = new SystemLocalClock();

var currentSid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
using var agentInstance = new EventWaitHandle(false, EventResetMode.ManualReset,
    $"Local\\BoundlessStudy.Agent.{currentSid}", out var createdAgentInstance);
if (!createdAgentInstance)
{
    Console.WriteLine("BoundlessStudy.Agent: another per-user Agent instance is already running; exiting without touching its session.");
    return 0;
}

await using var store = new SqliteDataStore(options, clock);

try
{
    var result = await store.InitializeAsync();

    Console.WriteLine(
        $"BoundlessStudy.Agent: database ready " +
        $"(schema {result.Handshake.SchemaVersion}; " +
        $"local profile {(result.Seed.LocalProfileCreated ? "created" : "present")}, " +
        $"focus profiles +{result.Seed.FocusProfilesCreated}, " +
        $"achievement definitions +{result.Seed.AchievementDefinitionsCreated}).");

    if (result.BackupRecordWarning is not null)
    {
        Console.Error.WriteLine($"Warning: {result.BackupRecordWarning}");
    }

    // Configure recovery options
    var recoveryOptions = new AgentRecoveryOptions();
    bool runOnce = false;
    int timeoutMs = -1;
    string? pipeName = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] is "--run-once" or "--once")
        {
            runOnce = true;
        }
        else if (args[i] is "--interval-ms" && i + 1 < args.Length && int.TryParse(args[i + 1], out var ms))
        {
            recoveryOptions.SweepInterval = TimeSpan.FromMilliseconds(ms);
            i++;
        }
        else if (args[i] is "--interval-seconds" && i + 1 < args.Length && int.TryParse(args[i + 1], out var sec))
        {
            recoveryOptions.SweepInterval = TimeSpan.FromSeconds(sec);
            i++;
        }
        else if (args[i] is "--timeout-ms" && i + 1 < args.Length && int.TryParse(args[i + 1], out var tms))
        {
            timeoutMs = tms;
            i++;
        }
        else if (args[i] is "--pipe-name" && i + 1 < args.Length)
        {
            pipeName = args[i + 1];
            i++;
        }
        else if (args[i] is "--self-test")
        {
            return await AgentSelfTest.RunAsync(store, clock);
        }
    }

    var executor = new RecoverySweepExecutor(store, clock);
    await using var worker = new BackgroundRecoveryWorker(executor, recoveryOptions);

    if (runOnce)
    {
        // Startup sweep only
        await worker.RunStartupSweepAsync();
        return 0;
    }

    // Any nonterminal session left Active/Blocked/SystemPaused after a previous
    // Agent process exited is recoverable interruption. Do not silently resume a
    // session or credit downtime; use the existing 24-hour recovery flow.
    await using (var startupRepos = store.CreateRepositories())
    {
        var localProfile = await startupRepos.LocalProfiles.FindSingleAsync(CancellationToken.None);
        if (localProfile is not null)
        {
            var priorSession = await startupRepos.FocusSessions.FindActiveByProfileAsync(localProfile.Id, CancellationToken.None);
            if (priorSession is { IsActive: true })
            {
                var startupRecorder = new BoundlessStudy.Application.EventRecording.SessionEventRecorder(
                    startupRepos.SessionEvents, clock);
                var interrupt = new BoundlessStudy.Application.UseCases.MarkFocusInterruptedHandler(
                    startupRepos.FocusSessions, startupRepos.UnitOfWork, clock, startupRecorder);
                await interrupt.HandleAsync(priorSession.Id, BoundlessStudy.Domain.FocusEndReason.AgentCrash,
                    CancellationToken.None, clock.UtcNow);
                Console.WriteLine("BoundlessStudy.Agent: previous active session moved to the existing recovery flow.");
            }
        }
    }

    var requestHandler = new AgentRequestHandler(store, clock);
    var applicationBlockingCoordinator = new ApplicationBlockingCoordinator(store, clock);
    requestHandler.AttachApplicationBlockingCoordinator(applicationBlockingCoordinator);
    await using var ipcServer = new AgentIpcServer(requestHandler, pipeName);
    using var systemStateCoordinator = new SystemStateAgentCoordinator(store, clock);
    applicationBlockingCoordinator.SetProtectionFailureHandler(systemStateCoordinator.SetApplicationProtectionFailureAsync);
    systemStateCoordinator.Start();
    var browserProtectionMonitor = new BrowserProtectionMonitor(store, clock, requestHandler.IsBrowserProtectionReady);
    var bridgePipeName = IpcProtocol.BridgePipeNameForUser(ipcServer.ExpectedUserSid ?? "Default");
    await using var bridgeIpcServer = new AgentIpcServer(
        requestHandler,
        bridgePipeName,
        clientRole: AgentClientRole.BrowserBridge);

    using var cts = new CancellationTokenSource();
    if (timeoutMs > 0)
    {
        cts.CancelAfter(timeoutMs);
    }

    ConsoleCancelEventHandler cancelHandler = (_, e) =>
    {
        e.Cancel = true;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    };

    EventHandler exitHandler = (_, _) =>
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    };

    Console.CancelKeyPress += cancelHandler;
    AppDomain.CurrentDomain.ProcessExit += exitHandler;

    try
    {
        Console.WriteLine($"BoundlessStudy.Agent: starting IPC server on '{ipcServer.PipeName}'...");
        ipcServer.Start();
        Console.WriteLine($"BoundlessStudy.Agent: starting browser bridge IPC server on '{bridgeIpcServer.PipeName}'...");
        bridgeIpcServer.Start();
        await Task.WhenAll(
            worker.RunAsync(cts.Token),
            browserProtectionMonitor.RunAsync(cts.Token),
            applicationBlockingCoordinator.RunAsync(cts.Token));
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        AppDomain.CurrentDomain.ProcessExit -= exitHandler;
        await ipcServer.StopAsync();
        await bridgeIpcServer.StopAsync();
    }
    return 0;
}
catch (OperationCanceledException)
{
    return 0;
}
catch (PersistenceException ex)
{
    Console.Error.WriteLine($"BoundlessStudy.Agent: {ex.Code}: {ex.Message}");
    return 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"BoundlessStudy.Agent: fatal error: {ex.Message}");
    return 1;
}
