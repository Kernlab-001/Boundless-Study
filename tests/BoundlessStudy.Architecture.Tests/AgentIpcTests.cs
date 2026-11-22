using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using BoundlessStudy.Agent;
using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Contracts.Ipc;
using BoundlessStudy.Contracts.Recovery;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;

namespace BoundlessStudy.Architecture.Tests;

public sealed class AgentIpcTests : IAsyncDisposable
{
    private readonly string _testRoot;
    private readonly SqliteDataStore _store;
    private readonly TestLocalClock _clock;
    private readonly AgentRequestHandler _handler;

    public AgentIpcTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), "BoundlessStudy.AgentIpcTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testRoot);

        var options = new SqliteDatabaseOptions
        {
            DatabasePath = Path.Combine(_testRoot, PersistenceConstants.DatabaseFileName),
            BackupDirectory = Path.Combine(_testRoot, PersistenceConstants.BackupFolderName),
            Pooling = false,
            BusyTimeoutMilliseconds = 2_000,
            MigrationMutexWaitMilliseconds = 5_000,
            MigrationLockProbeTimeoutMilliseconds = 250,
            AutomaticBackupRetentionCount = 7,
        };

        _clock = new TestLocalClock();
        _store = new SqliteDataStore(options, _clock);
        _store.InitializeAsync().GetAwaiter().GetResult();
        _handler = new AgentRequestHandler(_store, _clock);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        try
        {
            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task Ping_operation_returns_ok_status()
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.PingOperation,
            DateTime.UtcNow);

        var response = await _handler.HandleRequestAsync(request);

        Assert.True(response.Success);
        Assert.Equal(request.RequestId, response.RequestId);
        Assert.NotNull(response.PayloadJson);
        Assert.Contains("\"status\":\"ok\"", response.PayloadJson);
    }

    [Fact]
    public async Task ProfileProgress_operation_returns_progress_dto()
    {
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GetProfileProgressOperation,
            DateTime.UtcNow);

        var response = await _handler.HandleRequestAsync(request);

        Assert.True(response.Success);
        Assert.NotNull(response.PayloadJson);

        var progress = JsonSerializer.Deserialize<ProfileProgressDto>(response.PayloadJson, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(progress);
        Assert.Equal(0, progress.TotalXp);
        Assert.Equal(1, progress.Level);
        Assert.Equal(300, progress.DailySessionXpCap);
    }

    [Fact]
    public async Task Blocking_operations_are_role_scoped_and_strict_requires_verified_empty_policy()
    {
        var desktopAttempt = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.BridgeGetPolicyOperation, DateTime.UtcNow);
        var denied = await _handler.HandleRequestAsync(desktopAttempt);
        Assert.False(denied.Success);
        Assert.Equal(IpcErrors.UnauthorizedClient, denied.ErrorCode);

        var addSite = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.AddBlockedSiteOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new AddBlockedSiteRequest("example.com", "Example"), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(addSite)).Success);

        var startStrict = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new StartFocusSessionRequest("Strict", 1500, null), IpcFrameCodec.SerializerOptions));
        var rejectedStart = await _handler.HandleRequestAsync(startStrict);
        Assert.False(rejectedStart.Success);
        Assert.Equal("BROWSER_PROTECTION_UNAVAILABLE", rejectedStart.ErrorCode);

        var hello = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.BridgeHelloOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new BridgeHelloRequest("jjfgoeghdckpppcfjinhcccffmkmkibn", "0.1.0", "chrome", 1), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(hello, clientRole: AgentClientRole.BrowserBridge)).Success);

        var applied = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.BridgePolicyAppliedOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new BridgePolicyAppliedRequest("jjfgoeghdckpppcfjinhcccffmkmkibn", "chrome", null, true, DateTime.UtcNow, null), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(applied, clientRole: AgentClientRole.BrowserBridge)).Success);

        var acceptedStart = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new StartFocusSessionRequest("Strict", 1500, null), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(acceptedStart)).Success);
    }

    [Fact]
    public async Task Active_session_snapshot_reports_idle_and_checkpoints_cannot_credit_idle_study_time()
    {
        var profiles = await _handler.HandleRequestAsync(new IpcRequest(
            IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.ListFocusProfilesOperation, DateTime.UtcNow));
        Assert.True(profiles.Success);
        var focusProfiles = JsonSerializer.Deserialize<FocusProfileDto[]>(profiles.PayloadJson!, IpcFrameCodec.SerializerOptions);
        var profile = Assert.Single(focusProfiles!, p => p.Code == BuiltInProfiles.BalancedCode);

        var started = await _handler.HandleRequestAsync(new IpcRequest(
            IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new StartFocusSessionRequest(profile.Code, 1500, null), IpcFrameCodec.SerializerOptions)));
        Assert.True(started.Success);
        using var startedJson = JsonDocument.Parse(started.PayloadJson!);
        var sessionId = startedJson.RootElement.GetProperty("id").GetGuid();

        await using (var repos = _store.CreateRepositories())
        {
            var session = await repos.FocusSessions.FindAsync(sessionId);
            Assert.NotNull(session);
            var systemState = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock,
                new SessionEventRecorder(repos.SessionEvents, _clock));
            var beganIdle = await systemState.HandleUserIdleAsync(sessionId, true, TimeSpan.FromMinutes(5),
                occurredAtUtc: _clock.UtcNow);
            Assert.True(beganIdle.Success);
        }

        var snapshot = await _handler.HandleRequestAsync(new IpcRequest(
            IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.GetActiveFocusSessionOperation, DateTime.UtcNow));
        Assert.True(snapshot.Success);
        var active = JsonSerializer.Deserialize<FocusSessionDto>(snapshot.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(active);
        Assert.True(active.IsIdle);

        var checkpoint = await _handler.HandleRequestAsync(new IpcRequest(
            IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.ApplyCheckpointOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new ApplyFocusCheckpointRequest(sessionId, 60, 60, 0, 0, 0, 0, 0), IpcFrameCodec.SerializerOptions)));
        Assert.True(checkpoint.Success);

        await using var verifyRepos = _store.CreateRepositories();
        var persisted = await verifyRepos.FocusSessions.FindAsync(sessionId);
        Assert.NotNull(persisted);
        Assert.Equal(60, persisted.ElapsedSeconds);
        Assert.Equal(0, persisted.ActiveStudySeconds);
    }

    [Theory]
    [InlineData(SessionState.Blocked)]
    [InlineData(SessionState.SystemPaused)]
    public async Task Agent_rejects_stale_desktop_checkpoints_while_session_is_paused(SessionState pausedState)
    {
        var startResponse = await _handler.HandleRequestAsync(new IpcRequest(
            IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new StartFocusSessionRequest(BuiltInProfiles.BalancedCode, 1500, null), IpcFrameCodec.SerializerOptions)));
        Assert.True(startResponse.Success);
        using var startJson = JsonDocument.Parse(startResponse.PayloadJson!);
        var sessionId = startJson.RootElement.GetProperty("id").GetGuid();

        await using (var repos = _store.CreateRepositories())
        {
            if (pausedState == SessionState.Blocked)
            {
                var block = new FocusBlockStateHandler(repos.FocusSessions, repos.UnitOfWork, _clock,
                    new SessionEventRecorder(repos.SessionEvents, _clock));
                Assert.True((await block.HandleAsync(sessionId, blocked: true)).Success);
            }
            else
            {
                var systemState = new SystemStateUseCases(repos.FocusSessions, repos.UnitOfWork, _clock,
                    new SessionEventRecorder(repos.SessionEvents, _clock));
                Assert.True((await systemState.HandleWorkstationLockAsync(sessionId, isLocked: true)).Success);
            }
        }

        var response = await _handler.HandleRequestAsync(new IpcRequest(
            IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.ApplyCheckpointOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new ApplyFocusCheckpointRequest(sessionId, 90, 90, 0, 0, 0, 0, 0), IpcFrameCodec.SerializerOptions)));
        Assert.False(response.Success);
        Assert.Equal(IpcErrors.SessionNotActive, response.ErrorCode);

        await using var verifyRepos = _store.CreateRepositories();
        var persisted = await verifyRepos.FocusSessions.FindAsync(sessionId);
        Assert.NotNull(persisted);
        Assert.Equal(pausedState, persisted.State);
        Assert.Equal(0, persisted.ElapsedSeconds);
        Assert.Equal(0, persisted.ActiveStudySeconds);
    }

    [Fact]
    public async Task Browser_violation_requires_a_blocked_domain_and_records_without_pausing_focus()
    {
        var addSite = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.AddBlockedSiteOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new AddBlockedSiteRequest("example.com", null), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(addSite)).Success);

        var start = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new StartFocusSessionRequest("Balanced", 1500, null), IpcFrameCodec.SerializerOptions));
        var startResponse = await _handler.HandleRequestAsync(start);
        Assert.True(startResponse.Success);
        var sessionId = JsonDocument.Parse(startResponse.PayloadJson!).RootElement.GetProperty("id").GetGuid();

        var hello = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.BridgeHelloOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new BridgeHelloRequest("jjfgoeghdckpppcfjinhcccffmkmkibn", "0.1.0", "edge", 1), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(hello, clientRole: AgentClientRole.BrowserBridge)).Success);
        var applied = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.BridgePolicyAppliedOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new BridgePolicyAppliedRequest("jjfgoeghdckpppcfjinhcccffmkmkibn", "edge", sessionId, true, DateTime.UtcNow, null), IpcFrameCodec.SerializerOptions));
        Assert.True((await _handler.HandleRequestAsync(applied, clientRole: AgentClientRole.BrowserBridge)).Success);

        var violation = new IpcRequest(IpcProtocol.ProtocolVersion, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            IpcProtocol.BridgeReportViolationOperation, DateTime.UtcNow,
            JsonSerializer.Serialize(new BridgeViolationReportRequest(sessionId, "sub.example.com", DateTime.UtcNow), IpcFrameCodec.SerializerOptions));
        var response = await _handler.HandleRequestAsync(violation, clientRole: AgentClientRole.BrowserBridge);
        Assert.True(response.Success);

        await using var repos = _store.CreateRepositories();
        var session = await repos.FocusSessions.FindAsync(sessionId);
        Assert.NotNull(session);
        Assert.Equal(BoundlessStudy.Domain.SessionState.Active, session.State);
        var events = await repos.SessionEvents.ListBySessionAsync(BoundlessStudy.Domain.SessionType.Focus, sessionId);
        Assert.Contains(events, e => e.EventType == BoundlessStudy.Domain.SessionEventType.Blocked && e.PayloadJson.Contains("sub.example.com", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Subject_crud_operations_dispatch_and_persist_cleanly()
    {
        // 1. Create Subject
        var createPayload = JsonSerializer.Serialize(new CreateSubjectRequest("Computer Science", "Core modules"), IpcFrameCodec.SerializerOptions);
        var createReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CreateSubjectOperation,
            DateTime.UtcNow,
            createPayload);

        var createResp = await _handler.HandleRequestAsync(createReq);
        Assert.True(createResp.Success);

        var createdSubject = JsonSerializer.Deserialize<SubjectDto>(createResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(createdSubject);
        Assert.NotEqual(Guid.Empty, createdSubject.Id);
        Assert.Equal("Computer Science", createdSubject.Name);

        // 2. List Subjects
        var listReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListSubjectsOperation,
            DateTime.UtcNow);

        var listResp = await _handler.HandleRequestAsync(listReq);
        Assert.True(listResp.Success);
        var subjects = JsonSerializer.Deserialize<List<SubjectDto>>(listResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(subjects);
        Assert.Contains(subjects, s => s.Id == createdSubject.Id && s.Name == "Computer Science");

        // 3. Update Subject
        var updatePayload = JsonSerializer.Serialize(new UpdateSubjectRequest(createdSubject.Id, "CS and Systems", "Updated details"), IpcFrameCodec.SerializerOptions);
        var updateReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.UpdateSubjectOperation,
            DateTime.UtcNow,
            updatePayload);

        var updateResp = await _handler.HandleRequestAsync(updateReq);
        Assert.True(updateResp.Success);

        // 4. Archive Subject
        var archivePayload = JsonSerializer.Serialize(new ArchiveSubjectRequest(createdSubject.Id, false), IpcFrameCodec.SerializerOptions);
        var archiveReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ArchiveSubjectOperation,
            DateTime.UtcNow,
            archivePayload);

        var archiveResp = await _handler.HandleRequestAsync(archiveReq);
        Assert.True(archiveResp.Success);

        var listRespAfterArchive = await _handler.HandleRequestAsync(listReq);
        var subjectsAfter = JsonSerializer.Deserialize<List<SubjectDto>>(listRespAfterArchive.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.Contains(subjectsAfter!, s => s.Id == createdSubject.Id && s.IsArchived);
    }

    [Fact]
    public async Task Task_lifecycle_operations_dispatch_and_complete_with_rewards()
    {
        // 1. Create Task
        var createPayload = JsonSerializer.Serialize(
            new CreateTaskRequest("Finish Milestone 4", "Agent and IPC implementation", null, null, 60, "High", null),
            IpcFrameCodec.SerializerOptions);

        var createReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CreateTaskOperation,
            DateTime.UtcNow,
            createPayload);

        var createResp = await _handler.HandleRequestAsync(createReq);
        Assert.True(createResp.Success);
        var createdTask = JsonSerializer.Deserialize<StudyTaskDto>(createResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(createdTask);

        // 2. Start Task
        var startPayload = JsonSerializer.Serialize(new StartTaskRequest(createdTask.Id), IpcFrameCodec.SerializerOptions);
        var startReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.StartTaskOperation,
            DateTime.UtcNow,
            startPayload);
        var startResp = await _handler.HandleRequestAsync(startReq);
        Assert.True(startResp.Success);

        // 3. Complete Task
        var completePayload = JsonSerializer.Serialize(new CompleteTaskRequest(createdTask.Id), IpcFrameCodec.SerializerOptions);
        var completeReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CompleteTaskOperation,
            DateTime.UtcNow,
            completePayload);
        var completeResp = await _handler.HandleRequestAsync(completeReq);
        Assert.True(completeResp.Success);

        var outcome = JsonSerializer.Deserialize<SessionOutcomeDto>(completeResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(outcome);
        Assert.True(outcome.Success);
        Assert.True(outcome.TotalXp > 0);

        // 4. Reopen Task
        var reopenPayload = JsonSerializer.Serialize(new ReopenTaskRequest(createdTask.Id), IpcFrameCodec.SerializerOptions);
        var reopenReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ReopenTaskOperation,
            DateTime.UtcNow,
            reopenPayload);
        var reopenResp = await _handler.HandleRequestAsync(reopenReq);
        Assert.True(reopenResp.Success);

        // 5. Soft-Delete Task
        var deletePayload = JsonSerializer.Serialize(new SoftDeleteTaskRequest(createdTask.Id), IpcFrameCodec.SerializerOptions);
        var deleteReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.SoftDeleteTaskOperation,
            DateTime.UtcNow,
            deletePayload);
        var deleteResp = await _handler.HandleRequestAsync(deleteReq);
        Assert.True(deleteResp.Success);
    }

    [Fact]
    public async Task Focus_session_start_checkpoint_complete_and_history_operations()
    {
        // 1. List Focus Profiles
        var profilesReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListFocusProfilesOperation,
            DateTime.UtcNow);
        var profilesResp = await _handler.HandleRequestAsync(profilesReq);
        Assert.True(profilesResp.Success);
        var profiles = JsonSerializer.Deserialize<List<FocusProfileDto>>(profilesResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(profiles);
        Assert.NotEmpty(profiles);

        // 2. Start Focus Session
        var startPayload = JsonSerializer.Serialize(new StartFocusSessionRequest(profiles[0].Code, 1500, null), IpcFrameCodec.SerializerOptions);
        var startReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.StartFocusSessionOperation,
            DateTime.UtcNow,
            startPayload);
        var startResp = await _handler.HandleRequestAsync(startReq);
        Assert.True(startResp.Success);

        using var doc = JsonDocument.Parse(startResp.PayloadJson!);
        var sessionId = doc.RootElement.GetProperty("id").GetGuid();

        // 3. Get Active Session
        var activeReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.GetActiveFocusSessionOperation,
            DateTime.UtcNow);
        var activeResp = await _handler.HandleRequestAsync(activeReq);
        Assert.True(activeResp.Success);
        var activeDto = JsonSerializer.Deserialize<FocusSessionDto>(activeResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(activeDto);
        Assert.Equal(sessionId, activeDto.Id);

        // 4. Checkpoint
        var checkpointPayload = JsonSerializer.Serialize(
            new ApplyFocusCheckpointRequest(sessionId, 600, 600, 0, 0, 0, 0, 0),
            IpcFrameCodec.SerializerOptions);
        var checkpointReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ApplyCheckpointOperation,
            DateTime.UtcNow,
            checkpointPayload);
        var checkpointResp = await _handler.HandleRequestAsync(checkpointReq);
        Assert.True(checkpointResp.Success);

        // 5. Complete Session
        var completePayload = JsonSerializer.Serialize(
            new CompleteFocusSessionRequest(sessionId, 1500, 1500, 0, 0),
            IpcFrameCodec.SerializerOptions);
        var completeReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CompleteFocusSessionOperation,
            DateTime.UtcNow,
            completePayload);
        var completeResp = await _handler.HandleRequestAsync(completeReq);
        Assert.True(completeResp.Success);
        var outcome = JsonSerializer.Deserialize<SessionOutcomeDto>(completeResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(outcome);
        Assert.True(outcome.Success);
        Assert.True(outcome.AwardXp > 0);

        // 6. List Session History
        var historyReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ListSessionHistoryOperation,
            DateTime.UtcNow);
        var historyResp = await _handler.HandleRequestAsync(historyReq);
        Assert.True(historyResp.Success);
        var history = JsonSerializer.Deserialize<List<FocusSessionDto>>(historyResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(history);
        Assert.Contains(history, s => s.Id == sessionId);
    }

    [Fact]
    public async Task Recovery_operations_getState_resume_discard()
    {
        // 1. Recovery GetState when no session is interrupted
        var getReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.RecoveryGetStateOperation,
            DateTime.UtcNow);
        var getResp = await _handler.HandleRequestAsync(getReq);
        Assert.True(getResp.Success);
        var state = JsonSerializer.Deserialize<RecoveryState>(getResp.PayloadJson!, IpcFrameCodec.SerializerOptions);
        Assert.NotNull(state);
        Assert.Null(state.Pending);

        // 2. Resume with missing session ID fails cleanly
        var resumeReq = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.RecoveryResumeOperation,
            DateTime.UtcNow,
            "{}");
        var resumeResp = await _handler.HandleRequestAsync(resumeReq);
        Assert.False(resumeResp.Success);
        Assert.Equal(IpcErrors.InvalidRequest, resumeResp.ErrorCode);
    }

    [Fact]
    public async Task Idempotency_cache_returns_identical_response_for_duplicate_key()
    {
        var idempotencyKey = "key-" + Guid.NewGuid().ToString("N");
        var createPayload = JsonSerializer.Serialize(new CreateSubjectRequest("Physics", "Science"), IpcFrameCodec.SerializerOptions);

        var firstRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CreateSubjectOperation,
            DateTime.UtcNow,
            createPayload,
            IdempotencyKey: idempotencyKey);

        var firstResponse = await _handler.HandleRequestAsync(firstRequest);
        Assert.True(firstResponse.Success);
        var firstSubject = JsonSerializer.Deserialize<SubjectDto>(firstResponse.PayloadJson!, IpcFrameCodec.SerializerOptions);

        // Send a second request with same idempotency key and new request ID
        var secondRequestId = Guid.NewGuid().ToString("D");
        var secondRequest = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            secondRequestId,
            Guid.NewGuid().ToString("D"),
            IpcProtocol.CreateSubjectOperation,
            DateTime.UtcNow,
            createPayload,
            IdempotencyKey: idempotencyKey);

        var secondResponse = await _handler.HandleRequestAsync(secondRequest);
        Assert.True(secondResponse.Success);
        Assert.Equal(secondRequestId, secondResponse.RequestId);

        var secondSubject = JsonSerializer.Deserialize<SubjectDto>(secondResponse.PayloadJson!, IpcFrameCodec.SerializerOptions);
        // Returns the identical subject ID cached from the first operation
        Assert.Equal(firstSubject!.Id, secondSubject!.Id);
    }

    [Fact]
    public async Task Error_sanitization_strips_paths_and_stack_traces()
    {
        // Malformed request with non-existent session
        var request = new IpcRequest(
            IpcProtocol.ProtocolVersion,
            Guid.NewGuid().ToString("D"),
            Guid.NewGuid().ToString("D"),
            IpcProtocol.ApplyCheckpointOperation,
            DateTime.UtcNow,
            JsonSerializer.Serialize(new ApplyFocusCheckpointRequest(Guid.NewGuid(), 10, 10, 0, 0, 0, 0, 0), IpcFrameCodec.SerializerOptions));

        var response = await _handler.HandleRequestAsync(request);

        Assert.False(response.Success);
        Assert.Equal(IpcErrors.SessionNotFound, response.ErrorCode);
        Assert.NotNull(response.ErrorMessage);
        Assert.DoesNotContain(":\\", response.ErrorMessage);
        Assert.DoesNotContain("at BoundlessStudy", response.ErrorMessage);
    }

    [Fact]
    public async Task Named_pipe_server_and_client_roundtrip_successfully()
    {
        var pipeName = "BoundlessStudy.TestPipe." + Guid.NewGuid().ToString("N");
        await using var server = new AgentIpcServer(_handler, pipeName);
        server.Start();

        await using var client = new StudyAgentClient(pipeName, connectTimeoutMs: 3000);

        // Ping
        var pingOk = await client.PingAsync();
        Assert.True(pingOk);

        // Get Progress
        var progress = await client.GetProfileProgressAsync();
        Assert.NotNull(progress);
        Assert.Equal(1, progress.Level);

        // Create Subject
        var subject = await client.CreateSubjectAsync(new CreateSubjectRequest("Philosophy", "Logic and ethics"));
        Assert.NotNull(subject);
        Assert.Equal("Philosophy", subject.Name);

        // List Subjects
        var subjects = await client.GetSubjectsAsync();
        Assert.Contains(subjects, s => s.Id == subject.Id);

        // Create Task
        var task = await client.CreateTaskAsync(new CreateTaskRequest("Read Logic Book", "Chapters 1-3", subject.Id, null, 45, "Normal", null));
        Assert.NotNull(task);
        Assert.Equal("Read Logic Book", task.Title);

        // Complete Task
        var completed = await client.CompleteTaskAsync(new CompleteTaskRequest(task.Id));
        Assert.NotNull(completed);
        Assert.Equal("Done", completed.Status);

        // Focus session lifecycle over pipe
        var profiles = await client.ListFocusProfilesAsync();
        Assert.NotEmpty(profiles);

        var sessionId = await client.StartFocusSessionAsync(new StartFocusSessionRequest(profiles[0].Code, 1500, task.Id));
        Assert.NotEqual(Guid.Empty, sessionId);

        var active = await client.GetActiveFocusSessionAsync();
        Assert.NotNull(active);
        Assert.Equal(sessionId, active.Id);

        var outcome = await client.CompleteFocusSessionAsync(new CompleteFocusSessionRequest(sessionId, 1500, 1500, 0, 0));
        Assert.True(outcome.Success);
        Assert.True(outcome.AwardXp > 0);

        // Recovery via IRecoveryClient interface
        var recoveryState = await client.GetStateAsync();
        Assert.True(recoveryState.Success);
        Assert.NotNull(recoveryState.State);
    }

    [Fact]
    public async Task Named_pipe_client_times_out_with_agent_unavailable_when_server_missing()
    {
        var missingPipeName = "BoundlessStudy.MissingPipe." + Guid.NewGuid().ToString("N");
        await using var client = new StudyAgentClient(missingPipeName, connectTimeoutMs: 500);

        var ping = await client.PingAsync();
        Assert.False(ping);

        var recoveryResp = await client.GetStateAsync();
        Assert.False(recoveryResp.Success);
        Assert.Equal(RecoveryErrors.AgentUnavailable, recoveryResp.ErrorCode);
    }

    [Fact]
    public async Task Named_pipe_server_rejects_unauthorized_client_sid()
    {
        var pipeName = "BoundlessStudy.SecTest." + Guid.NewGuid().ToString("N");
        // Set expected SID to a dummy SID that will never match the connecting client
        var fakeSid = "S-1-5-21-9999999999-8888888888-7777777777-6666";

        await using var server = new AgentIpcServer(_handler, pipeName, expectedUserSid: fakeSid);
        server.Start();

        await using var client = new StudyAgentClient(pipeName, connectTimeoutMs: 1500);

        var ping = await client.PingAsync();
        Assert.False(ping);
    }

    [Fact]
    public async Task MainWindowViewModel_Navigates_To_Subjects_And_Tasks_Over_Real_Ipc_And_Persists_To_Database()
    {
        var pipeName = "BoundlessStudy.DesktopNavTest." + Guid.NewGuid().ToString("N");
        await using var server = new AgentIpcServer(_handler, pipeName);
        server.Start();

        await using var client = new StudyAgentClient(pipeName, connectTimeoutMs: 3000);
        await using var mainVm = new BoundlessStudy.Desktop.ViewModels.MainWindowViewModel(client, clock: _clock);
        await mainVm.InitializeAsync();

        Assert.True(mainVm.IsConnected, "Main window must be connected to agent over IPC.");

        // 1. Navigate to Subjects and create a Subject via ViewModel
        mainVm.NavigateToSubjects();
        Assert.IsType<BoundlessStudy.Desktop.ViewModels.SubjectsViewModel>(mainVm.CurrentViewModel);
        var subjectsVm = (BoundlessStudy.Desktop.ViewModels.SubjectsViewModel)mainVm.CurrentViewModel;
        while (subjectsVm.IsLoading)
        {
            await Task.Delay(20);
        }

        subjectsVm.Name = "Operating Systems";
        subjectsVm.Description = "Kernels and concurrency";
        await subjectsVm.CreateCommand.ExecuteAsync(null);

        // Verify subject persisted in database via IPC
        var subjects = await client.GetSubjectsAsync();
        var createdSubject = Assert.Single(subjects, s => s.Name == "Operating Systems");
        Assert.Equal("Kernels and concurrency", createdSubject.Description);

        // 2. Navigate to Tasks and create a Task via ViewModel
        mainVm.NavigateToTasks();
        Assert.IsType<BoundlessStudy.Desktop.ViewModels.TasksViewModel>(mainVm.CurrentViewModel);
        var tasksVm = (BoundlessStudy.Desktop.ViewModels.TasksViewModel)mainVm.CurrentViewModel;
        while (tasksVm.IsLoading)
        {
            await Task.Delay(20);
        }

        tasksVm.Title = "Implement Kernel Scheduler";
        tasksVm.Notes = "Priority queue implementation";
        tasksVm.Priority = "High";
        await tasksVm.CreateCommand.ExecuteAsync(null);

        // Verify task persisted in database via IPC
        var tasks = await client.GetTasksAsync();
        var createdTask = Assert.Single(tasks, t => t.Title == "Implement Kernel Scheduler");
        Assert.Equal("High", createdTask.Priority);

        // 3. Complete task via ViewModel and verify reward applied
        tasksVm.SelectedTask = createdTask;
        await tasksVm.CompleteCommand.ExecuteAsync(createdTask);

        var progress = await client.GetProfileProgressAsync();
        Assert.NotNull(progress);
        Assert.True(progress.TotalXp >= 10, "Task completion must award XP over real IPC.");
        Assert.Equal(1, progress.CompletedTaskCount);
    }

    private sealed class TestLocalClock : ILocalClock
    {
        public DateTime UtcNow { get; set; } = new(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        public int UtcOffsetMinutes { get; set; } = 0;
        public DateOnly ToLocalDate(DateTime utcDateTime) =>
            DateOnly.FromDateTime(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc).AddMinutes(UtcOffsetMinutes));
    }
}
