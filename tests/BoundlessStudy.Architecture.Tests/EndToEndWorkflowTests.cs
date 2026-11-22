using System.Buffers.Binary;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using BoundlessStudy.Agent;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Desktop.ViewModels;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Infrastructure.Persistence;
using Xunit;

namespace BoundlessStudy.Architecture.Tests;

/// <summary>
/// Independent Security, Quality, and End-to-End Workflow Verification for Boundless Study Milestone 4.
/// Executes automated acceptance tests against disposable databases and real Named Pipe IPC.
/// Audits security boundaries, unauthorized SID rejection, frame sanitization, and production data isolation.
/// </summary>
public sealed class EndToEndWorkflowTests
{
    private const string ExpectedBaselineDbHash = "D017FF11AE73DEC6C406734F0AE2CB46E94BD71E8447C1CC13CD13A895C08443";

    [Fact]
    public async Task EndToEnd_Milestone4_Full_Acceptance_Flow_Persists_And_Recovers_After_Application_Restart()
    {
        var (testRoot, options) = CreateDisposableDbOptions();
        var clock = new TestLocalClock();
        var pipeName = "BoundlessStudy.TestPipe.E2E." + Guid.NewGuid().ToString("N");

        try
        {
            Guid subjectId;
            Guid taskId;
            Guid sessionId;
            int awardXp;
            long totalXp;
            int levelAfter;

            // =========================================================================
            // PHASE 1: Launch Application & Connect Desktop Client to Agent via Named Pipe
            // =========================================================================
            await using (var store = new SqliteDataStore(options, clock))
            {
                await store.InitializeAsync();
                var handler = new AgentRequestHandler(store, clock);
                await using var server = new AgentIpcServer(handler, pipeName);
                server.Start();

                await using var client = new StudyAgentClient(pipeName, connectTimeoutMs: 3000);

                // 1. Desktop connects to Agent via Named Pipe
                var pingOk = await client.PingAsync();
                Assert.True(pingOk, "Desktop client must connect and ping the Agent Named Pipe successfully.");

                var focusClient = new NamedPipeFocusClient(pipeName, connectTimeoutMs: 3000);
                var recoveryClient = new NamedPipeRecoveryClient(pipeName, connectTimeoutMs: 3000);
                var mainVm = new MainWindowViewModel(focusClient, recoveryClient, clock);
                await mainVm.InitializeAsync();
                Assert.True(mainVm.IsConnected, "MainWindowViewModel must transition to Connected status.");
                Assert.False(mainVm.HasPendingRecovery, "Initial state must have no pending recovery.");

                // 2. Create a Subject
                var createdSubject = await client.CreateSubjectAsync(
                    new CreateSubjectRequest("Distributed Systems", "Core architectures and consensus protocols"));
                Assert.NotNull(createdSubject);
                Assert.NotEqual(Guid.Empty, createdSubject.Id);
                Assert.Equal("Distributed Systems", createdSubject.Name);
                subjectId = createdSubject.Id;

                // Verify Subject listing over IPC
                var subjects = await client.GetSubjectsAsync();
                Assert.Contains(subjects, s => s.Id == subjectId && s.Name == "Distributed Systems");

                // 3. Create a Task linked to the Subject
                var createdTask = await client.CreateTaskAsync(new CreateTaskRequest(
                    Title: "Implement Raft Consensus",
                    Notes: "Leader election, log replication, and persistence",
                    SubjectId: subjectId,
                    DueDateLocal: null,
                    EstimatedMinutes: 45,
                    Priority: "High",
                    PlannedForLocalDate: null));
                Assert.NotNull(createdTask);
                Assert.NotEqual(Guid.Empty, createdTask.Id);
                Assert.Equal("Implement Raft Consensus", createdTask.Title);
                Assert.Equal(subjectId, createdTask.SubjectId);
                taskId = createdTask.Id;

                // Verify task listing resolves SubjectName
                var tasks = await client.GetTasksAsync();
                var taskInList = tasks.FirstOrDefault(t => t.Id == taskId);
                Assert.NotNull(taskInList);
                Assert.Equal("Distributed Systems", taskInList.SubjectName);

                // 4. Start a Focus Session linked to the Task
                var profiles = await client.ListFocusProfilesAsync();
                Assert.NotEmpty(profiles);
                var activeProfile = profiles.First(p => p.IsEnabled);

                const int plannedSeconds = 1500; // 25-minute Pomodoro session
                sessionId = await client.StartFocusSessionAsync(
                    new StartFocusSessionRequest(activeProfile.Code, plannedSeconds, taskId));
                Assert.NotEqual(Guid.Empty, sessionId);

                // Verify active focus session via IPC
                var activeSession = await client.GetActiveFocusSessionAsync();
                Assert.NotNull(activeSession);
                Assert.Equal(sessionId, activeSession.Id);
                Assert.Equal(activeProfile.Code, activeSession.ProfileCode);
                Assert.Equal("Implement Raft Consensus", activeSession.TaskTitleSnapshot);
                Assert.Equal("Distributed Systems", activeSession.SubjectNameSnapshot);

                // 5. Record Study Activity (Checkpoints / Elapsed Time)
                // Checkpoint 1: 600s elapsed
                clock.UtcNow = clock.UtcNow.AddSeconds(600);
                await client.ApplyFocusCheckpointAsync(new ApplyFocusCheckpointRequest(
                    sessionId,
                    ElapsedSeconds: 600,
                    ActiveStudySeconds: 600,
                    IdleSeconds: 0,
                    ProtectionGapSeconds: 0,
                    ViolationCount: 0,
                    ExceptionCount: 0,
                    RecoveryCount: 0));

                var chk1 = await client.GetActiveFocusSessionAsync();
                Assert.NotNull(chk1);
                Assert.Equal(600, chk1.ElapsedSeconds);
                Assert.Equal(600, chk1.ActiveStudySeconds);

                // Checkpoint 2: 1500s elapsed (planned duration reached)
                clock.UtcNow = clock.UtcNow.AddSeconds(900);
                await client.ApplyFocusCheckpointAsync(new ApplyFocusCheckpointRequest(
                    sessionId,
                    ElapsedSeconds: 1500,
                    ActiveStudySeconds: 1500,
                    IdleSeconds: 0,
                    ProtectionGapSeconds: 0,
                    ViolationCount: 0,
                    ExceptionCount: 0,
                    RecoveryCount: 0));

                // 6. Complete the Focus Session
                var completionOutcome = await client.CompleteFocusSessionAsync(new CompleteFocusSessionRequest(
                    sessionId,
                    ElapsedSeconds: 1500,
                    ActiveStudySeconds: 1500,
                    IdleSeconds: 0,
                    ProtectionGapSeconds: 0));

                // 7. Verify Terminal Persistence (XP award, streak qualification, level)
                Assert.True(completionOutcome.Success, "Session completion must succeed.");
                Assert.True(completionOutcome.IsCompleted, "Session must reach terminal completed state.");
                Assert.True(completionOutcome.AwardXp > 0, "XP award must be strictly positive.");
                Assert.True(completionOutcome.TotalXp >= completionOutcome.AwardXp, "Total XP must reflect award.");
                Assert.True(completionOutcome.StreakQualified, "1500s active study must qualify for daily streak.");
                Assert.True(completionOutcome.LevelAfter >= completionOutcome.LevelBefore, "Level must not decrease.");

                awardXp = completionOutcome.AwardXp;
                totalXp = completionOutcome.TotalXp;
                levelAfter = completionOutcome.LevelAfter;

                // 8. Display Session Summary
                // Verify Session History DTO over IPC
                var history = await client.ListFocusSessionHistoryAsync(10);
                var completedSessionSummary = history.FirstOrDefault(s => s.Id == sessionId);
                Assert.NotNull(completedSessionSummary);
                Assert.Equal("Completed", completedSessionSummary.State);
                Assert.Equal(1500, completedSessionSummary.ActiveStudySeconds);
                Assert.Equal(awardXp, completedSessionSummary.XpEarned);
                Assert.Equal("Implement Raft Consensus", completedSessionSummary.TaskTitleSnapshot);
                Assert.Equal("Distributed Systems", completedSessionSummary.SubjectNameSnapshot);

                // Verify Session History ViewModel display
                var historyVm = new SessionHistoryViewModel(focusClient, mainVm);
                await historyVm.LoadHistoryAsync();
                Assert.NotEmpty(historyVm.Sessions);
                var historyItem = historyVm.Sessions.FirstOrDefault(s => s.Id == sessionId);
                Assert.NotNull(historyItem);
                Assert.Equal(SessionState.Completed, historyItem.State);
                Assert.Contains(awardXp.ToString(), historyItem.XpEarnedDisplay);

                // Verify Home ViewModel displays updated XP and streak
                var homeVm = new HomeViewModel(focusClient, recoveryClient, mainVm);
                await homeVm.InitializeAsync();
                Assert.Equal(totalXp, homeVm.TotalXp);
                Assert.Equal(1, homeVm.StreakDays);
                Assert.False(homeVm.HasActiveSession, "Active session card must clear after completion.");
                Assert.False(homeVm.HasInterruptedSession, "Must have no pending interrupted session.");
            }

            // =========================================================================
            // PHASE 2: Restart Application (New Client & Agent Session against SAME DB)
            // =========================================================================
            var restartPipeName = "BoundlessStudy.TestPipe.E2E.Restart." + Guid.NewGuid().ToString("N");

            await using (var restartedStore = new SqliteDataStore(options, clock))
            {
                // Reopen the exact same SQLite database file
                await restartedStore.InitializeAsync();
                var restartedHandler = new AgentRequestHandler(restartedStore, clock);
                await using var restartedServer = new AgentIpcServer(restartedHandler, restartPipeName);
                restartedServer.Start();

                await using var restartedClient = new StudyAgentClient(restartPipeName, connectTimeoutMs: 3000);

                var restartedPing = await restartedClient.PingAsync();
                Assert.True(restartedPing, "Restarted Desktop client must connect to the new Agent instance.");

                // 9. Query and verify all persisted data is recovered accurately
                // Verify Subject recovery
                var restoredSubjects = await restartedClient.GetSubjectsAsync();
                var restoredSubject = restoredSubjects.FirstOrDefault(s => s.Id == subjectId);
                Assert.NotNull(restoredSubject);
                Assert.Equal("Distributed Systems", restoredSubject.Name);
                Assert.Equal("Core architectures and consensus protocols", restoredSubject.Description);

                // Verify Task recovery
                var restoredTasks = await restartedClient.GetTasksAsync();
                var restoredTask = restoredTasks.FirstOrDefault(t => t.Id == taskId);
                Assert.NotNull(restoredTask);
                Assert.Equal("Implement Raft Consensus", restoredTask.Title);
                Assert.Equal(subjectId, restoredTask.SubjectId);
                Assert.Equal("Distributed Systems", restoredTask.SubjectName);

                // Verify Completed Session recovery in history
                var restoredHistory = await restartedClient.ListFocusSessionHistoryAsync(10);
                var restoredSession = restoredHistory.FirstOrDefault(s => s.Id == sessionId);
                Assert.NotNull(restoredSession);
                Assert.Equal("Completed", restoredSession.State);
                Assert.Equal(1500, restoredSession.ActiveStudySeconds);
                Assert.Equal(awardXp, restoredSession.XpEarned);
                Assert.Equal("Implement Raft Consensus", restoredSession.TaskTitleSnapshot);
                Assert.Equal("Distributed Systems", restoredSession.SubjectNameSnapshot);

                // Verify Profile Progress recovery (Total XP, Streak, Level)
                var restoredProgress = await restartedClient.GetProfileProgressAsync();
                Assert.NotNull(restoredProgress);
                Assert.Equal(totalXp, restoredProgress.TotalXp);
                Assert.Equal(levelAfter, restoredProgress.Level);
                Assert.Equal(1, restoredProgress.CurrentStreakDays);
                Assert.Equal(1, restoredProgress.QualifiedSessionCount);

                // Verify Active Session is null
                var restoredActive = await restartedClient.GetActiveFocusSessionAsync();
                Assert.Null(restoredActive);
            }
        }
        finally
        {
            CleanUpTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task EndToEnd_Interrupted_Session_Flow_Blocks_New_Sessions_And_Resume_Discard_Ensures_Single_Ledger_Entry()
    {
        var (testRoot, options) = CreateDisposableDbOptions();
        var clock = new TestLocalClock();
        var pipeName = "BoundlessStudy.TestPipe.Recovery." + Guid.NewGuid().ToString("N");

        try
        {
            await using var store = new SqliteDataStore(options, clock);
            await store.InitializeAsync();
            var handler = new AgentRequestHandler(store, clock);
            await using var server = new AgentIpcServer(handler, pipeName);
            server.Start();

            await using var client = new StudyAgentClient(pipeName, connectTimeoutMs: 3000);

            // 1. Start a Focus Session
            var profiles = await client.ListFocusProfilesAsync();
            Assert.NotEmpty(profiles);
            var profileCode = profiles[0].Code;

            const int plannedDuration = 1500;
            var sessionId = await client.StartFocusSessionAsync(
                new StartFocusSessionRequest(profileCode, plannedDuration, null));
            Assert.NotEqual(Guid.Empty, sessionId);

            // 2. Record 600s study activity
            clock.UtcNow = clock.UtcNow.AddSeconds(600);
            await client.ApplyFocusCheckpointAsync(new ApplyFocusCheckpointRequest(
                sessionId,
                ElapsedSeconds: 600,
                ActiveStudySeconds: 600,
                IdleSeconds: 0,
                ProtectionGapSeconds: 0,
                ViolationCount: 0,
                ExceptionCount: 0,
                RecoveryCount: 0));

            // 3. Simulate sudden interruption (e.g. process crash or power loss)
            await using (var repos = store.CreateRepositories())
            {
                var interruptHandler = new MarkFocusInterruptedHandler(
                    repos.FocusSessions,
                    repos.UnitOfWork,
                    clock,
                    new SessionEventRecorder(repos.SessionEvents, clock));

                var interruptResult = await interruptHandler.HandleAsync(sessionId, FocusEndReason.AgentCrash);
                Assert.True(interruptResult.Success, "Interruption simulation must succeed.");
            }

            // 4. Verify Desktop Recovery Client detects the interrupted session
            var recoveryState = await client.GetStateAsync();
            Assert.True(recoveryState.Success);
            Assert.NotNull(recoveryState.State?.Pending);
            Assert.Equal(sessionId, recoveryState.State!.Pending!.SessionId);
            Assert.Equal(600, recoveryState.State.Pending.ElapsedSeconds);
            Assert.False(recoveryState.State.Pending.IsExpired);

            // 5. Verify starting a NEW session is BLOCKED with RECOVERABLE_SESSION_PENDING
            var rawStartRequest = new IpcRequest(
                IpcProtocol.ProtocolVersion,
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                IpcProtocol.StartFocusSessionOperation,
                clock.UtcNow,
                JsonSerializer.Serialize(new StartFocusSessionRequest(profileCode, 900, null), IpcFrameCodec.SerializerOptions));

            var blockedResponse = await client.SendRawRequestAsync(rawStartRequest);
            Assert.False(blockedResponse.Success, "Starting a new session while an interrupted session is pending must fail.");
            Assert.Equal(IpcErrors.RecoverableSessionPending, blockedResponse.ErrorCode);
            Assert.Contains("interrupted session is awaiting recovery", blockedResponse.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            var ex = await Assert.ThrowsAsync<AgentException>(() =>
                client.StartFocusSessionAsync(new StartFocusSessionRequest(profileCode, 900, null)));
            Assert.Equal(IpcErrors.RecoverableSessionPending, ex.ErrorCode);

            // 6. Execute Resume Workflow
            // Session was at 600s < 1500s planned, so Resume restores it back to Active state
            var resumeResponse = await client.ResumeAsync(sessionId);
            Assert.True(resumeResponse.Success, "Resume of recoverable interrupted session must succeed.");

            var activeAfterResume = await client.GetActiveFocusSessionAsync();
            Assert.NotNull(activeAfterResume);
            Assert.Equal(sessionId, activeAfterResume.Id);
            Assert.Equal("Active", activeAfterResume.State);

            // 7. Simulate Interruption again before testing Discard
            await using (var repos = store.CreateRepositories())
            {
                var interruptHandler = new MarkFocusInterruptedHandler(
                    repos.FocusSessions,
                    repos.UnitOfWork,
                    clock,
                    new SessionEventRecorder(repos.SessionEvents, clock));

                await interruptHandler.HandleAsync(sessionId, FocusEndReason.AgentCrash);
            }

            // Verify session is pending recovery again
            var stateBeforeDiscard = await client.GetStateAsync();
            Assert.NotNull(stateBeforeDiscard.State?.Pending);
            Assert.Equal(sessionId, stateBeforeDiscard.State!.Pending!.SessionId);

            // 8. Execute Discard Workflow
            // Discard finalizes the session as Abandoned with a 0.5x partial reward
            var discardResponse = await client.DiscardAsync(sessionId);
            Assert.True(discardResponse.Success, "Discard must succeed.");
            Assert.Null(discardResponse.State?.Pending);

            // 9. Execute repeated Discard and Resume to verify Idempotent handling
            var repeatedDiscard = await client.DiscardAsync(sessionId);
            Assert.True(repeatedDiscard.Success, "Repeated Discard must succeed idempotently.");

            var repeatedResume = await client.ResumeAsync(sessionId);
            Assert.True(repeatedResume.Success, "Resume on an already-discarded session must return existing outcome idempotently.");

            // 10. Verify that EXACTLY ONE terminal reward and ledger entry was created
            await using (var repos = store.CreateRepositories())
            {
                var profile = await repos.LocalProfiles.FindSingleAsync();
                Assert.NotNull(profile);

                var ledgerEntries = await repos.RewardLedger.ListByProfileAsync(profile.Id);
                var sessionLedgerEntries = ledgerEntries
                    .Where(e => e.SourceId == sessionId.ToString())
                    .ToList();

                Assert.Single(sessionLedgerEntries); // EXACTLY ONE ledger entry
                var entry = sessionLedgerEntries[0];
                Assert.Equal(LedgerSourceType.SessionPartial, entry.SourceType);
                Assert.Equal(15, entry.Amount); // 10 active mins * 3 XP/min * 0.5 discard multiplier = 15 XP

                var totalXpInDb = await repos.RewardLedger.SumXpAsync(profile.Id);
                Assert.Equal(15, totalXpInDb);
            }

            // 11. Verify that new session creation is now UNBLOCKED
            var newSessionId = await client.StartFocusSessionAsync(
                new StartFocusSessionRequest(profileCode, 900, null));
            Assert.NotEqual(Guid.Empty, newSessionId);
            Assert.NotEqual(sessionId, newSessionId);
        }
        finally
        {
            CleanUpTestDirectory(testRoot);
        }
    }

    [Fact]
    public void Security_Audit_Desktop_Does_Not_Reference_Sqlite_Or_Infrastructure()
    {
        var repoRoot = FindRepoRoot();
        var desktopCsprojPath = Path.Combine(repoRoot, "src", "BoundlessStudy.Desktop", "BoundlessStudy.Desktop.csproj");
        Assert.True(File.Exists(desktopCsprojPath), "Desktop project file must exist.");

        var xml = XDocument.Load(desktopCsprojPath);

        // Verify ProjectReferences
        var projectReferences = xml.Descendants(xml.Root!.Name.Namespace + "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        Assert.NotEmpty(projectReferences);
        Assert.DoesNotContain(projectReferences, r => r.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(projectReferences, r => r.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));

        // Verify PackageReferences (Desktop must have NO PackageReferences)
        var packageReferences = xml.Descendants(xml.Root!.Name.Namespace + "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .ToList();

        Assert.Empty(packageReferences);

        // Verify Desktop assembly runtime dependencies
        var desktopAssembly = typeof(MainWindowViewModel).Assembly;
        var referencedAssemblyNames = desktopAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        Assert.DoesNotContain(referencedAssemblyNames, n => n.Contains("Infrastructure", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblyNames, n => n.Contains("Sqlite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(referencedAssemblyNames, n => n.Contains("SQLitePCLRaw", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Security_Audit_Agent_Named_Pipe_Server_Rejects_Unauthorized_Client_Sids()
    {
        var (testRoot, options) = CreateDisposableDbOptions();
        var clock = new TestLocalClock();
        var pipeName = "BoundlessStudy.SecAudit.Sid." + Guid.NewGuid().ToString("N");
        const string fakeUnauthorizedSid = "S-1-5-21-9999999999-8888888888-7777777777-6666";

        try
        {
            await using var store = new SqliteDataStore(options, clock);
            await store.InitializeAsync();
            var handler = new AgentRequestHandler(store, clock);

            // Server enforces fake SID which will never match the connecting client's identity
            await using var server = new AgentIpcServer(handler, pipeName, expectedUserSid: fakeUnauthorizedSid);
            server.Start();

            await using var client = new StudyAgentClient(pipeName, connectTimeoutMs: 2000);

            var pingRequest = new IpcRequest(
                IpcProtocol.ProtocolVersion,
                Guid.NewGuid().ToString("D"),
                Guid.NewGuid().ToString("D"),
                IpcProtocol.PingOperation,
                DateTime.UtcNow);

            var response = await client.SendRawRequestAsync(pingRequest);

            Assert.False(response.Success, "Server must reject request from unauthorized client SID.");
            Assert.Equal(IpcErrors.UnauthorizedClient, response.ErrorCode);
            Assert.Contains("not authorized", response.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CleanUpTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Security_Audit_Malformed_Json_And_Oversized_Frames_Are_Safely_Rejected()
    {
        var (testRoot, options) = CreateDisposableDbOptions();
        var clock = new TestLocalClock();
        var pipeName = "BoundlessStudy.SecAudit.Frame." + Guid.NewGuid().ToString("N");

        try
        {
            await using var store = new SqliteDataStore(options, clock);
            await store.InitializeAsync();
            var handler = new AgentRequestHandler(store, clock);
            await using var server = new AgentIpcServer(handler, pipeName);
            server.Start();

            // Test 1: Send Malformed JSON Frame (valid length prefix, corrupted content)
            {
                await using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(3000);

                var corruptedJsonBytes = Encoding.UTF8.GetBytes("{ broken json string: \"corrupted");
                var prefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, corruptedJsonBytes.Length);

                await pipeClient.WriteAsync(prefix);
                await pipeClient.WriteAsync(corruptedJsonBytes);
                await pipeClient.FlushAsync();

                var response = await IpcFrameCodec.ReadResponseAsync(pipeClient);
                Assert.NotNull(response);
                Assert.False(response.Success, "Malformed JSON must return failure response.");
                Assert.Equal(IpcErrors.ProtocolFailure, response.ErrorCode);
            }

            // Test 2: Send Frame Exceeding Maximum Allowed Size (1 MB)
            {
                await using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(3000);

                var prefix = new byte[4];
                // 1 MB + 1024 bytes exceeds IpcProtocol.MaxFrameBytes (1024 * 1024)
                BinaryPrimitives.WriteInt32LittleEndian(prefix, IpcProtocol.MaxFrameBytes + 1024);

                await pipeClient.WriteAsync(prefix);
                await pipeClient.FlushAsync();

                var response = await IpcFrameCodec.ReadResponseAsync(pipeClient);
                Assert.NotNull(response);
                Assert.False(response.Success, "Frame exceeding 1 MB must return failure response.");
                Assert.Equal(IpcErrors.ProtocolFailure, response.ErrorCode);
            }

            // Test 3: Send Negative Length Prefix Frame
            {
                await using var pipeClient = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipeClient.ConnectAsync(3000);

                var prefix = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(prefix, -50);

                await pipeClient.WriteAsync(prefix);
                await pipeClient.FlushAsync();

                var response = await IpcFrameCodec.ReadResponseAsync(pipeClient);
                Assert.NotNull(response);
                Assert.False(response.Success, "Negative frame length must return failure response.");
                Assert.Equal(IpcErrors.ProtocolFailure, response.ErrorCode);
            }

            // Test 4: Verify Server and Store Remain Healthy After Malformed Frames
            await using var normalClient = new StudyAgentClient(pipeName, connectTimeoutMs: 3000);
            var pingResult = await normalClient.PingAsync();
            Assert.True(pingResult, "Server must remain responsive and uncorrupted after rejecting invalid frames.");

            var progress = await normalClient.GetProfileProgressAsync();
            Assert.NotNull(progress);
        }
        finally
        {
            CleanUpTestDirectory(testRoot);
        }
    }

    [Fact]
    public async Task Security_Audit_Error_Responses_Do_Not_Leak_Secrets_Paths_Or_Stack_Traces()
    {
        var (testRoot, options) = CreateDisposableDbOptions();
        var clock = new TestLocalClock();

        try
        {
            await using var store = new SqliteDataStore(options, clock);
            await store.InitializeAsync();
            var handler = new AgentRequestHandler(store, clock);

            // Test various invalid requests to observe error responses
            var requests = new[]
            {
                // Non-existent session checkpoint
                new IpcRequest(
                    IpcProtocol.ProtocolVersion,
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    IpcProtocol.ApplyCheckpointOperation,
                    DateTime.UtcNow,
                    JsonSerializer.Serialize(new ApplyFocusCheckpointRequest(Guid.NewGuid(), 10, 10, 0, 0, 0, 0, 0), IpcFrameCodec.SerializerOptions)),

                // Non-existent task completion
                new IpcRequest(
                    IpcProtocol.ProtocolVersion,
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    IpcProtocol.CompleteTaskOperation,
                    DateTime.UtcNow,
                    JsonSerializer.Serialize(new CompleteTaskRequest(Guid.NewGuid()), IpcFrameCodec.SerializerOptions)),

                // Unknown operation
                new IpcRequest(
                    IpcProtocol.ProtocolVersion,
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    "nonexistent.operation.name",
                    DateTime.UtcNow),

                // Malformed inner payload
                new IpcRequest(
                    IpcProtocol.ProtocolVersion,
                    Guid.NewGuid().ToString("D"),
                    Guid.NewGuid().ToString("D"),
                    IpcProtocol.CreateSubjectOperation,
                    DateTime.UtcNow,
                    "{ invalid inner payload")
            };

            foreach (var req in requests)
            {
                var resp = await handler.HandleRequestAsync(req);
                Assert.False(resp.Success);
                Assert.NotNull(resp.ErrorMessage);

                // Verify no local file paths are leaked
                Assert.DoesNotMatch(@"[A-Za-z]:\\", resp.ErrorMessage);
                Assert.DoesNotContain(@"\Users\", resp.ErrorMessage, StringComparison.OrdinalIgnoreCase);

                // Verify no stack traces are leaked
                Assert.DoesNotContain("   at ", resp.ErrorMessage);
                Assert.DoesNotContain("at BoundlessStudy.", resp.ErrorMessage);
                Assert.DoesNotContain("--- End of stack trace", resp.ErrorMessage);
            }
        }
        finally
        {
            CleanUpTestDirectory(testRoot);
        }
    }

    [Fact]
    public void DataIntegrity_Audit_Production_Database_Untouched_And_Backup_Hash_Matches()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var boundlessDir = Path.Combine(localAppData, "BoundlessStudy");
        var productionDbPath = Path.Combine(boundlessDir, "boundless.db");
        var preBackupDbPath = Path.Combine(boundlessDir, "boundless-milestone4-pre-backup.db");

        // Some isolated CI/development profiles intentionally have no production data.
        // In that case both paths must remain absent; never create a database merely to
        // satisfy this audit. If either exists, require the complete expected pair/hash.
        if (!File.Exists(productionDbPath) && !File.Exists(preBackupDbPath))
        {
            Assert.False(File.Exists(productionDbPath));
            Assert.False(File.Exists(preBackupDbPath));
            return;
        }

        Assert.True(File.Exists(productionDbPath), $"Production database must exist at {productionDbPath}");
        Assert.True(File.Exists(preBackupDbPath), $"Pre-migration backup must exist at {preBackupDbPath}");

        var prodHash = ComputeSha256(productionDbPath);
        var backupHash = ComputeSha256(preBackupDbPath);

        Assert.Equal(ExpectedBaselineDbHash, prodHash);
        Assert.Equal(ExpectedBaselineDbHash, backupHash);
    }

    private static string ComputeSha256(string filePath)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var bytes = sha.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }

    private static (string root, SqliteDatabaseOptions options) CreateDisposableDbOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), "BoundlessStudy.E2E", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var options = new SqliteDatabaseOptions
        {
            DatabasePath = Path.Combine(root, PersistenceConstants.DatabaseFileName),
            BackupDirectory = Path.Combine(root, PersistenceConstants.BackupFolderName),
            Pooling = false,
            BusyTimeoutMilliseconds = 2_000,
            MigrationMutexWaitMilliseconds = 5_000,
            MigrationLockProbeTimeoutMilliseconds = 250,
            AutomaticBackupRetentionCount = 7,
        };

        return (root, options);
    }

    private static void CleanUpTestDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch { }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BoundlessStudy.slnx"))
                || File.Exists(Path.Combine(dir.FullName, "BoundlessStudy.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent!;
        }

        throw new InvalidOperationException("Could not locate the Boundless Study solution file above the test directory.");
    }

    private sealed class TestLocalClock : ILocalClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        public int UtcOffsetMinutes { get; set; } = 0;
        public DateOnly ToLocalDate(DateTime utcDateTime) =>
            DateOnly.FromDateTime(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc).AddMinutes(UtcOffsetMinutes));
    }
}
