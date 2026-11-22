using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Desktop.Services;
using BoundlessStudy.Desktop.ViewModels;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Architecture.Tests;

public sealed class FocusTimerSynchronizationTests
{
    [Fact]
    public async Task Refresh_WhenAgentReportsPause_UsesPersistedCheckpointAndStopsCounting()
    {
        var initial = Session(SessionState.Active, elapsed: 20, active: 18);
        var client = new TimerFocusClient(initial with
        {
            State = SessionState.SystemPaused,
            ElapsedSeconds = 19,
            ActiveStudySeconds = 17
        });
        using var viewModel = new ActiveFocusViewModel(initial, client, new NoopNavigation(), enableTimer: false);

        await viewModel.RefreshAgentStateAsync();

        Assert.Equal(SessionState.SystemPaused, viewModel.SessionState);
        Assert.Equal(19, viewModel.ElapsedSeconds);
        Assert.Equal(17, viewModel.ActiveStudySeconds);
        Assert.Contains("System Paused", viewModel.StateDisplay);
        Assert.False(viewModel.CanEndEarly);
        Assert.False(viewModel.CanComplete);
    }

    [Fact]
    public async Task Refresh_WhenAgentUnavailable_DisablesControlsAndRecoveryResumesFromAuthority()
    {
        var initial = Session(SessionState.Active, elapsed: 10, active: 9);
        var client = new TimerFocusClient(initial) { IsConnected = false };
        using var viewModel = new ActiveFocusViewModel(initial, client, new NoopNavigation(), enableTimer: false);

        await viewModel.RefreshAgentStateAsync();

        Assert.Contains("Agent unavailable", viewModel.StateDisplay);
        Assert.False(viewModel.CanEndEarly);
        Assert.False(viewModel.CanComplete);

        client.IsConnected = true;
        client.ActiveSession = initial with { ElapsedSeconds = 12, ActiveStudySeconds = 11 };
        await viewModel.RefreshAgentStateAsync();

        Assert.Equal(SessionState.Active, viewModel.SessionState);
        Assert.Equal(12, viewModel.ElapsedSeconds);
        Assert.Equal(11, viewModel.ActiveStudySeconds);
        Assert.True(viewModel.CanEndEarly);
    }

    [Fact]
    public async Task Refresh_WhenActiveSnapshotAdvancesCounters_PersistsCheckpoint()
    {
        var initial = Session(SessionState.Active, elapsed: 10, active: 10);
        var client = new TimerFocusClient(initial with { ElapsedSeconds = 15, ActiveStudySeconds = 15 });
        using var viewModel = new ActiveFocusViewModel(initial, client, new NoopNavigation(), enableTimer: false);

        await viewModel.RefreshAgentStateAsync();

        Assert.Equal(1, client.CheckpointCount);
        Assert.Equal(15, client.LastCheckpointElapsedSeconds);
        Assert.Equal(15, client.LastCheckpointActiveStudySeconds);
    }

    [Fact]
    public async Task Refresh_WhenActiveSessionNoLongerMatches_FreezesThisView()
    {
        var initial = Session(SessionState.Active, elapsed: 10, active: 10);
        var client = new TimerFocusClient(Session(SessionState.Active, elapsed: 50, active: 50));
        using var viewModel = new ActiveFocusViewModel(initial, client, new NoopNavigation(), enableTimer: false);

        await viewModel.RefreshAgentStateAsync();

        Assert.Contains("Agent unavailable", viewModel.StateDisplay);
        Assert.Equal(10, viewModel.ElapsedSeconds);
        Assert.False(viewModel.CanEndEarly);
    }

    [Fact]
    public async Task Refresh_WhenAgentReportsThresholdIdle_ClampsActiveStudyAndShowsPausedStudyMetric()
    {
        var initial = Session(SessionState.Active, elapsed: 400, active: 400);
        var client = new TimerFocusClient(initial with
        {
            IsIdle = true,
            IdleDurationSeconds = 3
        });
        using var viewModel = new ActiveFocusViewModel(initial, client, new NoopNavigation(), enableTimer: false);

        await viewModel.RefreshAgentStateAsync();

        Assert.True(viewModel.IsIdle);
        Assert.Equal(3, viewModel.IdleDurationSeconds);
        Assert.Equal(400, viewModel.ElapsedSeconds);
        Assert.Equal(400, viewModel.ActiveStudySeconds);
        Assert.Contains("active-study time paused", viewModel.StateDisplay);
        Assert.True(viewModel.CanEndEarly); // Idle does not pause the focus session itself.
    }

    private static FocusSessionSummary Session(SessionState state, int elapsed, int active) => new(
        Guid.NewGuid(),
        BuiltInProfiles.StrictCode,
        1500,
        elapsed,
        active,
        state,
        DateTime.UtcNow,
        null,
        null,
        null,
        0,
        false);

    private sealed class TimerFocusClient(FocusSessionSummary? activeSession) : IFocusClient
    {
        public FocusSessionSummary? ActiveSession { get; set; } = activeSession;
        public bool IsConnected { get; set; } = true;
        public int CheckpointCount { get; private set; }
        public int LastCheckpointElapsedSeconds { get; private set; }
        public int LastCheckpointActiveStudySeconds { get; private set; }

        public Task<bool> PingAgentAsync(CancellationToken cancellationToken = default) => Task.FromResult(IsConnected);
        public Task<ProfileProgress?> GetProfileProgressAsync(CancellationToken cancellationToken = default) => Task.FromResult<ProfileProgress?>(null);
        public Task<IReadOnlyList<FocusProfileSummary>> ListFocusProfilesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FocusProfileSummary>>([]);
        public Task<FocusSessionSummary?> GetActiveSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(ActiveSession);
        public Task<EntityResult> StartFocusSessionAsync(string profileCode, int plannedFocusSeconds, Guid? taskId = null, CancellationToken cancellationToken = default) => Task.FromResult(EntityResult.Fail("NOT_USED", "Not used by this test."));
        public Task ApplyCheckpointAsync(Guid sessionId, int elapsedSeconds, int activeStudySeconds, CancellationToken cancellationToken = default)
        {
            CheckpointCount++;
            LastCheckpointElapsedSeconds = elapsedSeconds;
            LastCheckpointActiveStudySeconds = activeStudySeconds;
            if (ActiveSession?.Id == sessionId)
            {
                ActiveSession = ActiveSession with
                {
                    ElapsedSeconds = Math.Max(ActiveSession.ElapsedSeconds, elapsedSeconds),
                    ActiveStudySeconds = Math.Max(ActiveSession.ActiveStudySeconds, activeStudySeconds)
                };
            }
            return Task.CompletedTask;
        }
        public Task<SessionCompletionOutcome> CompleteFocusSessionAsync(Guid sessionId, int elapsedSeconds, int activeStudySeconds, int idleSeconds = 0, int protectionGapSeconds = 0, CancellationToken cancellationToken = default) => Task.FromResult(SessionCompletionOutcome.Failed("NOT_USED", "Not used by this test."));
        public Task<SessionCompletionOutcome> EndFocusEarlyAsync(Guid sessionId, int elapsedSeconds, int activeStudySeconds, int idleSeconds, int protectionGapSeconds, FocusEndReason reason, CancellationToken cancellationToken = default) => Task.FromResult(SessionCompletionOutcome.Failed("NOT_USED", "Not used by this test."));
        public Task<IReadOnlyList<FocusSessionSummary>> ListSessionHistoryAsync(int maxCount = 50, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<FocusSessionSummary>>([]);
        public Task<IReadOnlyList<StudyTaskSummary>> ListTasksAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StudyTaskSummary>>([]);
        public Task<IReadOnlyList<SubjectSummary>> ListSubjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SubjectSummary>>([]);
    }

    private sealed class NoopNavigation : INavigationService
    {
        public void NavigateToHome() { }
        public void NavigateToFocusSetup() { }
        public void NavigateToActiveSession(FocusSessionSummary session) { }
        public void NavigateToHistory() { }
        public void NavigateToSubjects() { }
        public void NavigateToTasks() { }
        public void NavigateToRecovery() { }
        public Task ShowRecoveryDialogAsync() => Task.CompletedTask;
    }
}
