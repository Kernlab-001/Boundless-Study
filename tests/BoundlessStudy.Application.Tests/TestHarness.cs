using BoundlessStudy.Application.EventRecording;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// Wires the application handlers to in-memory fakes, seeds one local profile and
/// the three built-in focus profiles, and offers small helpers used by the tests.
/// </summary>
public sealed class TestHarness
{
    public FakeClock Clock { get; } = new();

    public InMemoryLocalProfileRepository Profiles { get; } = new();
    public InMemorySubjectRepository Subjects { get; } = new();
    public InMemoryStudyTaskRepository Tasks { get; } = new();
    public InMemoryFocusProfileRepository FocusProfiles { get; } = new();
    public InMemoryFocusSessionRepository Sessions { get; } = new();
    public InMemoryBreakSessionRepository Breaks { get; } = new();
    public InMemorySessionEventRepository Events { get; } = new();
    public InMemorySessionExceptionRepository Exceptions { get; } = new();
    public InMemoryBlockedAppRepository BlockedApps { get; } = new();
    public InMemoryBlockedSiteRepository BlockedSites { get; } = new();
    public InMemoryAllowedSiteRepository AllowedSites { get; } = new();
    public InMemoryRewardLedgerRepository Ledger { get; } = new();
    public InMemoryAchievementRepository Achievements { get; } = new();
    public InMemoryUserAchievementRepository UserAchievements { get; } = new();
    public InMemoryAppSettingRepository Settings { get; } = new();
    public InMemoryBackupRecordRepository Backups { get; } = new();

    public FakeUnitOfWork UnitOfWork { get; } = new();

    public LocalProfile Profile { get; }

    public RewardService Rewards { get; }
    public SessionEventRecorder EventRecorder { get; }
    public RecoverFocusSessionHandler RecoverSession { get; }
    public StartFocusSessionHandler StartSession { get; }
    public ApplyFocusCheckpointHandler Checkpoint { get; }
    public GetPendingFocusSessionHandler PendingSession { get; }
    public FocusBlockStateHandler BlockState { get; }
    public FocusSystemPauseHandler SystemPause { get; }
    public MarkFocusInterruptedHandler Interrupt { get; }
    public CompleteFocusSessionHandler CompleteSession { get; }
    public EndFocusEarlyHandler EndEarly { get; }
    public AbandonFocusSessionHandler Abandon { get; }
    public ResumeFocusRecoveryHandler ResumeRecovery { get; }
    public DiscardFocusRecoveryHandler DiscardRecovery { get; }
    public FocusRecoveryCoordinator RecoveryCoordinator { get; }
    public SetFocusProtectionHandler SetProtection { get; }

    public CreateSubjectHandler CreateSubject { get; }
    public UpdateSubjectHandler UpdateSubject { get; }
    public ArchiveSubjectHandler ArchiveSubject { get; }
    public ListSubjectsHandler ListSubjects { get; }
    public CreateTaskHandler CreateTask { get; }
    public UpdateTaskHandler UpdateTask { get; }
    public CompleteTaskHandler CompleteTask { get; }
    public ReopenTaskHandler ReopenTask { get; }
    public StartTaskHandler StartTask { get; }
    public ArchiveTaskHandler ArchiveTask { get; }
    public SoftDeleteTaskHandler DeleteTask { get; }
    public ListTasksHandler ListTasks { get; }

    public StartBreakHandler StartBreak { get; }
    public FinishBreakHandler FinishBreak { get; }
    public InterruptBreakHandler InterruptBreak { get; }

    public AddBlockedAppHandler AddBlockedApp { get; }
    public AddBlockedSiteHandler AddBlockedSite { get; }
    public AddAllowedSiteHandler AddAllowedSite { get; }

    public GrantSessionExceptionHandler GrantException { get; }

    public GetProfileProgressHandler GetProgress { get; }

    public TestHarness()
    {
        Profile = LocalProfile.Create(Clock.UtcNow, "Test profile");
        Profiles.AddAsync(Profile).GetAwaiter().GetResult();

        var rewardState = new FakeRewardStateProvider(Sessions, Tasks, Clock);
        Rewards = new RewardService(Ledger, Achievements, UserAchievements, rewardState, Clock);
        EventRecorder = new SessionEventRecorder(Events, Clock);

        RecoverSession = new RecoverFocusSessionHandler(Sessions, Profiles, UnitOfWork, Rewards, Clock, EventRecorder);
        Abandon = new AbandonFocusSessionHandler(Sessions, Clock, RecoverSession);
        ResumeRecovery = new ResumeFocusRecoveryHandler(Profiles, Sessions, RecoverSession);
        DiscardRecovery = new DiscardFocusRecoveryHandler(Profiles, Sessions, Abandon);
        StartSession = new StartFocusSessionHandler(Profiles, FocusProfiles, Sessions, Breaks, Tasks, Subjects, UnitOfWork, Clock, EventRecorder, Abandon);
        Checkpoint = new ApplyFocusCheckpointHandler(Sessions, UnitOfWork, Clock);
        PendingSession = new GetPendingFocusSessionHandler(Profiles, Sessions, Clock);
        RecoveryCoordinator = new FocusRecoveryCoordinator(Profiles, Sessions, Clock, PendingSession, ResumeRecovery, DiscardRecovery);
        BlockState = new FocusBlockStateHandler(Sessions, UnitOfWork, Clock, EventRecorder);
        SystemPause = new FocusSystemPauseHandler(Sessions, UnitOfWork, Clock, EventRecorder);
        Interrupt = new MarkFocusInterruptedHandler(Sessions, UnitOfWork, Clock, EventRecorder);
        CompleteSession = new CompleteFocusSessionHandler(Sessions, Clock, RecoverSession);
        EndEarly = new EndFocusEarlyHandler(Sessions, Clock, RecoverSession);
        SetProtection = new SetFocusProtectionHandler(Sessions, UnitOfWork, Clock);

        CreateSubject = new CreateSubjectHandler(Profiles, Subjects, UnitOfWork, Clock);
        UpdateSubject = new UpdateSubjectHandler(Subjects, UnitOfWork, Clock);
        ArchiveSubject = new ArchiveSubjectHandler(Subjects, UnitOfWork, Clock);
        ListSubjects = new ListSubjectsHandler(Profiles, Subjects);
        CreateTask = new CreateTaskHandler(Profiles, Tasks, Subjects, UnitOfWork, Clock);
        UpdateTask = new UpdateTaskHandler(Tasks, Subjects, UnitOfWork, Clock);
        CompleteTask = new CompleteTaskHandler(Profiles, Tasks, UnitOfWork, Rewards, Clock);
        ReopenTask = new ReopenTaskHandler(Tasks, UnitOfWork, Clock);
        StartTask = new StartTaskHandler(Tasks, UnitOfWork, Clock);
        ArchiveTask = new ArchiveTaskHandler(Tasks, UnitOfWork, Clock);
        DeleteTask = new SoftDeleteTaskHandler(Tasks, UnitOfWork, Clock);
        ListTasks = new ListTasksHandler(Profiles, Tasks, Subjects);

        StartBreak = new StartBreakHandler(Sessions, Breaks, UnitOfWork, Clock, EventRecorder);
        FinishBreak = new FinishBreakHandler(Breaks, UnitOfWork, Clock, EventRecorder);
        InterruptBreak = new InterruptBreakHandler(Breaks, UnitOfWork, Clock);

        AddBlockedApp = new AddBlockedAppHandler(BlockedApps, Profiles, UnitOfWork, Clock);
        AddBlockedSite = new AddBlockedSiteHandler(BlockedSites, Profiles, UnitOfWork, Clock);
        AddAllowedSite = new AddAllowedSiteHandler(AllowedSites, Profiles, UnitOfWork, Clock);

        GrantException = new GrantSessionExceptionHandler(Sessions, FocusProfiles, Exceptions, UnitOfWork, Clock, EventRecorder);

        GetProgress = new GetProfileProgressHandler(Profiles, Ledger, rewardState, Clock);
    }

    public Guid CreateTaskAndGetId(string title = "Task")
    {
        var result = CreateTask.HandleAsync(Profile.Id, title).GetAwaiter().GetResult();
        Assert.True(result.Success);
        return result.Id!.Value;
    }

    public Guid StartSessionAndGetId(string profileCode = "Balanced", int plannedMinutes = 25, Guid? taskId = null)
    {
        var result = StartSession.HandleAsync(Profile.Id, profileCode, plannedMinutes * 60, taskId).GetAwaiter().GetResult();
        Assert.True(result.Success, result.Error?.Message);
        return result.Id!.Value;
    }

    /// <summary>Runs a normal completion with the given counters through the real handler.</summary>
    public SessionCompletionOutcome Complete(Guid sessionId, int activeMinutes, int? plannedMinutes = null)
    {
        var seconds = activeMinutes * 60;
        var planned = (plannedMinutes ?? Math.Max(5, activeMinutes)) * 60;
        return CompleteSession.HandleAsync(sessionId, planned, seconds, 0, 0).GetAwaiter().GetResult();
    }

    /// <summary>Adds prior session XP for the current local date (the daily-cap setup).</summary>
    public void SeedPriorSessionXp(int amount, string note = "prior")
    {
        var entry = RewardLedgerEntry.Create(
            Profile.Id,
            CurrencyCode.Xp,
            amount,
            LedgerSourceType.SessionCompletion,
            $"{note}-{Guid.NewGuid()}",
            Clock.UtcNow,
            Clock.Today,
            Clock.UtcOffsetMinutes,
            "ECONOMY-1");
        Ledger.AddAsync(entry).GetAwaiter().GetResult();
    }
}
