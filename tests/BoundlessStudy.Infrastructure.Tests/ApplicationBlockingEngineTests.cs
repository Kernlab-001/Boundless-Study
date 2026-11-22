using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Services;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Economy;
using BoundlessStudy.Domain.Entities;
using BoundlessStudy.Infrastructure.Persistence;
using BoundlessStudy.Infrastructure.Services;
using System.Reflection;
using Xunit;

namespace BoundlessStudy.Infrastructure.Tests;

/// <summary>
/// Fake implementation of <see cref="IWindowInterop"/> for deterministic unit tests.
/// </summary>
internal sealed class FakeWindowInterop : IWindowInterop
{
    public nint ForegroundHandle { get; set; } = 1001;
    public int ProcessId { get; set; } = 4242;
    public string? ProcessName { get; set; } = "discord";
    public string? ExecutablePath { get; set; } = @"C:\Users\test\AppData\Local\Discord\Discord.exe";

    public bool MinimizeSuccess { get; set; } = true;
    public bool CloseRequestSuccess { get; set; } = true;

    public int MinimizeCallCount { get; private set; }
    public int CloseRequestCallCount { get; private set; }

    public nint LastMinimizedHandle { get; private set; }
    public nint LastClosedHandle { get; private set; }

    public nint GetForegroundWindowHandle() => ForegroundHandle;

    public int GetWindowProcessId(nint hWnd) => hWnd == ForegroundHandle ? ProcessId : 0;

    public (string? ProcessName, string? ExecutablePath) GetProcessDetails(int processId) =>
        processId == ProcessId ? (ProcessName, ExecutablePath) : (null, null);

    public bool MinimizeWindow(nint hWnd)
    {
        MinimizeCallCount++;
        LastMinimizedHandle = hWnd;
        return MinimizeSuccess;
    }

    public bool RequestCloseWindow(nint hWnd)
    {
        CloseRequestCallCount++;
        LastClosedHandle = hWnd;
        return CloseRequestSuccess;
    }

    public void ResetCounts()
    {
        MinimizeCallCount = 0;
        CloseRequestCallCount = 0;
        LastMinimizedHandle = 0;
        LastClosedHandle = 0;
    }
}

public sealed class ApplicationBlockingEngineTests
{
    private static readonly DateTime TestStartTime = new(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Evaluate_WhenNoActiveSession_ReturnsNoActiveSession()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var fakeWindow = new FakeWindowInterop();

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        var result = await engine.EvaluateForegroundAppAsync();

        Assert.Equal(InterventionStatus.NoActiveSession, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task ConcurrentEvaluations_AreSerializedForSharedRepositories()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, _) = await SetupActiveSessionAsync(repos, db.Clock);
        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "notepad",
            ExecutablePath = @"C:\Windows\System32\notepad.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        var evaluations = Enumerable.Range(0, 8)
            .Select(_ => engine.EvaluateForegroundAppAsync(session.Id));

        var results = await Task.WhenAll(evaluations);

        Assert.All(results, result => Assert.Equal(InterventionStatus.NotBlocked, result.Status));
    }

    [Fact]
    public async Task Evaluate_WhenForegroundHandleZero_ReturnsNoForegroundWindow()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, _) = await SetupActiveSessionAsync(repos, db.Clock);

        var fakeWindow = new FakeWindowInterop
        {
            ForegroundHandle = 0
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NoForegroundWindow, result.Status);
        Assert.False(result.Intervened);
    }

    [Fact]
    public async Task Evaluate_WhenAppIsNotBlocked_ReturnsNotBlocked()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, _) = await SetupActiveSessionAsync(repos, db.Clock);

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "notepad",
            ExecutablePath = @"C:\Windows\System32\notepad.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NotBlocked, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task PathSpecificRule_DoesNotMatchDifferentExecutableWithSameName()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);
        await repos.BlockedApps.AddAsync(BlockedApp.Create(profileId, "Game", @"C:\Games\game.exe", null, db.Clock.UtcNow));
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop { ProcessName = "game", ExecutablePath = @"C:\OtherVendor\game.exe" };
        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions, repos.BlockedApps, repos.SessionExceptions, repos.FocusProfiles,
            db.Clock, repos.SessionEvents, fakeWindow);

        engine.StartMonitoring(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(10);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NotBlocked, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
    }

    [Fact]
    public async Task Evaluate_WhenRuleIsDisabled_DoesNotIntervene()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var disabledRule = BlockedApp.Create(profileId, "Steam", null, "steam.exe", db.Clock.UtcNow);
        disabledRule.SetEnabled(false, db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(disabledRule);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "steam",
            ExecutablePath = @"C:\Games\steam.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // Even after 5 seconds, disabled rule is not enforced
        db.Clock.UtcNow = db.Clock.UtcNow.AddSeconds(5);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NotBlocked, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task GracePeriod_Under3Seconds_DoesNotIntervene()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // T = 0s: enters foreground
        var result1 = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.GracePeriodActive, result1.Status);
        Assert.False(result1.Intervened);
        Assert.Equal(TimeSpan.Zero, result1.ForegroundDuration);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);

        // T = 1.5s: still in grace period
        db.Clock.UtcNow = db.Clock.UtcNow.AddSeconds(1.5);
        var result2 = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.GracePeriodActive, result2.Status);
        Assert.False(result2.Intervened);
        Assert.Equal(TimeSpan.FromSeconds(1.5), result2.ForegroundDuration);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);

        // T = 2.9s: still under 3 seconds
        db.Clock.UtcNow = TestStartTime.AddSeconds(2.9);
        var result3 = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.GracePeriodActive, result3.Status);
        Assert.False(result3.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task GracePeriod_At3SecondsContinuous_RequestsVisibleOverlay()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // T = 0s: start
        _ = await engine.EvaluateForegroundAppAsync(session.Id);

        // T = 3.0s: continuous foreground threshold reached!
        db.Clock.UtcNow = TestStartTime.AddSeconds(3.0);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.OverlayRequired, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(InterventionType.None, result.InterventionType);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public async Task GracePeriod_SwitchAwayBefore3Seconds_ResetsGracePeriod()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // T = 0s: Discord foreground
        _ = await engine.EvaluateForegroundAppAsync(session.Id);

        // T = 2.0s: Discord still foreground
        db.Clock.UtcNow = TestStartTime.AddSeconds(2.0);
        _ = await engine.EvaluateForegroundAppAsync(session.Id);

        // T = 2.5s: User switches away to Notepad (unblocked app)
        db.Clock.UtcNow = TestStartTime.AddSeconds(2.5);
        fakeWindow.ProcessName = "notepad";
        fakeWindow.ExecutablePath = @"C:\Windows\System32\notepad.exe";
        var switchResult = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.NotBlocked, switchResult.Status);

        // T = 3.0s: User switches back to Discord -> grace period must be reset to 0
        db.Clock.UtcNow = TestStartTime.AddSeconds(3.0);
        fakeWindow.ProcessName = "discord";
        fakeWindow.ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe";
        var backResult = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.GracePeriodActive, backResult.Status);
        Assert.False(backResult.Intervened);
        Assert.Equal(TimeSpan.Zero, backResult.ForegroundDuration);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);

        // T = 4.5s: 1.5s after switch back -> still in grace
        db.Clock.UtcNow = TestStartTime.AddSeconds(4.5);
        var intermediateResult = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.GracePeriodActive, intermediateResult.Status);
        Assert.False(intermediateResult.Intervened);

        // T = 6.1s: 3.1s continuous after switch back -> requests the visible overlay.
        db.Clock.UtcNow = TestStartTime.AddSeconds(6.1);
        var finalResult = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.OverlayRequired, finalResult.Status);
        Assert.False(finalResult.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
    }

    [Fact]
    public async Task GracePeriod_SwitchingBetweenDifferentBlockedApps_TracksIndependently()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var ruleDiscord = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        var ruleSpotify = BlockedApp.Create(profileId, "Spotify", null, "spotify.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(ruleDiscord);
        await repos.BlockedApps.AddAsync(ruleSpotify);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // T = 0s: Discord
        _ = await engine.EvaluateForegroundAppAsync(session.Id);

        // T = 2.0s: Switch directly to Spotify (different blocked app)
        db.Clock.UtcNow = TestStartTime.AddSeconds(2.0);
        fakeWindow.ProcessName = "spotify";
        fakeWindow.ExecutablePath = @"C:\Users\test\AppData\Local\Spotify\Spotify.exe";
        var spotifyResult1 = await engine.EvaluateForegroundAppAsync(session.Id);

        // Spotify grace starts at 0s, does not inherit Discord's 2s
        Assert.Equal(InterventionStatus.GracePeriodActive, spotifyResult1.Status);
        Assert.Equal(TimeSpan.Zero, spotifyResult1.ForegroundDuration);

        // T = 3.0s: Spotify has only been foreground for 1.0s
        db.Clock.UtcNow = TestStartTime.AddSeconds(3.0);
        var spotifyResult2 = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.GracePeriodActive, spotifyResult2.Status);
        Assert.False(spotifyResult2.Intervened);

        // T = 5.1s: Spotify has been foreground for 3.1s -> overlay requested
        db.Clock.UtcNow = TestStartTime.AddSeconds(5.1);
        var spotifyResult3 = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.OverlayRequired, spotifyResult3.Status);
        Assert.False(spotifyResult3.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
    }

    [Fact]
    public async Task AllowedApps_ConfiguredApp_BypassesIntervention()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        // Block VS Code
        var blockedApp = BlockedApp.Create(profileId, "VS Code", null, "code.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "code",
            ExecutablePath = @"C:\Program Files\Microsoft VS Code\Code.exe"
        };

        var options = new BlockingEngineOptions
        {
            AllowedApplications = ["code.exe"]
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow,
            options);

        engine.StartMonitoring(session.Id);

        // Even after 10 seconds continuous in foreground, it is allowed
        db.Clock.UtcNow = TestStartTime.AddSeconds(10.0);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.AllowedApp, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task AddAllowedApplication_DynamicRegistration_BypassesIntervention()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Notepad++", null, "notepad++.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "notepad++",
            ExecutablePath = @"C:\Program Files\Notepad++\notepad++.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);
        engine.AddAllowedApplication("notepad++");

        db.Clock.UtcNow = TestStartTime.AddSeconds(5.0);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.AllowedApp, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task ActiveSessionException_BypassesInterventionUntilExpired()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Game", null, "game.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);

        // Grant 5-minute exception from T=0 to T=300s
        var exception = SessionException.Create(
            session.Id,
            ExceptionTargetType.Application,
            "game.exe",
            TestStartTime,
            TestStartTime.AddSeconds(300),
            ExceptionReasonCategory.StudyResource,
            TestStartTime);
        await repos.SessionExceptions.AddAsync(exception);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "game",
            ExecutablePath = @"C:\Games\game.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // T = 50s: exception is active
        db.Clock.UtcNow = TestStartTime.AddSeconds(50);
        var activeExResult = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.ExceptionActive, activeExResult.Status);
        Assert.False(activeExResult.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);

        // T = 301s: exception has expired!
        db.Clock.UtcNow = TestStartTime.AddSeconds(301);
        var postExpiryStart = await engine.EvaluateForegroundAppAsync(session.Id);
        // Grace period starts anew once exception expires
        Assert.Equal(InterventionStatus.GracePeriodActive, postExpiryStart.Status);

        // T = 305s: 4s continuous past expiry -> intervened!
        db.Clock.UtcNow = TestStartTime.AddSeconds(305);
        var intervenedResult = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.Equal(InterventionStatus.OverlayRequired, intervenedResult.Status);
        Assert.False(intervenedResult.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
    }

    [Fact]
    public async Task ActiveSessionException_ForDifferentSession_DoesNotBypass()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Game", null, "game.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);

        // Exception granted for a different historical session GUID
        var otherSession = FocusSession.Start(
            profileId,
            BuiltInProfiles.BalancedCode,
            600,
            TestStartTime.AddDays(-1),
            EconomyRules.Version);
        otherSession.Complete(TestStartTime.AddDays(-1).AddMinutes(10), 600, 600, 0, 0);
        await repos.FocusSessions.AddAsync(otherSession);

        var exception = SessionException.Create(
            otherSession.Id,
            ExceptionTargetType.Application,
            "game.exe",
            TestStartTime,
            TestStartTime.AddSeconds(300),
            ExceptionReasonCategory.Technical,
            TestStartTime);
        await repos.SessionExceptions.AddAsync(exception);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "game",
            ExecutablePath = @"C:\Games\game.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        _ = await engine.EvaluateForegroundAppAsync(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(4);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.OverlayRequired, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
    }

    [Theory]
    [InlineData("narrator.exe")]
    [InlineData("magnify.exe")]
    [InlineData("osk.exe")]
    [InlineData("voiceaccess.exe")]
    [InlineData("livecaptions.exe")]
    [InlineData("atbroker.exe")]
    [InlineData("explorer.exe")]
    [InlineData("svchost.exe")]
    [InlineData("csrss.exe")]
    [InlineData("dwm.exe")]
    [InlineData("lsass.exe")]
    [InlineData("services.exe")]
    [InlineData("smss.exe")]
    [InlineData("wininit.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("system")]
    [InlineData("boundlessstudy.desktop.exe")]
    [InlineData("boundlessstudy.desktop")]
    [InlineData("boundlessstudy.agent.exe")]
    [InlineData("boundlessstudy.agent")]
    [InlineData("boundlessstudy.service.exe")]
    [InlineData("boundlessstudy.service")]
    [InlineData("boundlessstudy.bridge.exe")]
    [InlineData("boundlessstudy.bridge")]
    public async Task Revalidation_All24ProtectedTargets_NeverInterveneEvenIfMisconfigured(string protectedTarget)
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        // Simulate a legacy/corrupt rule that bypassed creation-time validation.
        // Runtime protection must still reject it immediately before intervention.
        var misconfiguredRule = BlockedApp.Create(profileId, "Misconfigured", null, "ordinary.exe", db.Clock.UtcNow);
        typeof(BlockedApp).GetProperty(nameof(BlockedApp.NormalizedExecutableName), BindingFlags.Instance | BindingFlags.Public)!
            .SetValue(misconfiguredRule, protectedTarget.ToLowerInvariant());
        await repos.BlockedApps.AddAsync(misconfiguredRule);
        await repos.Context.SaveChangesAsync();

        var stem = protectedTarget.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? protectedTarget[..^4]
            : protectedTarget;

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = stem,
            ExecutablePath = $@"C:\Windows\System32\{protectedTarget}"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // Protected targets bypass immediately, even when a misconfigured rule names them.
        var initialResult = await engine.EvaluateForegroundAppAsync(session.Id);
        Assert.True(initialResult.IsProtectedTarget);
        Assert.Equal(InterventionStatus.ProtectedTargetBypass, initialResult.Status);

        // Revalidate again after the grace interval to prove a delayed intervention cannot occur.
        db.Clock.UtcNow = TestStartTime.AddSeconds(10.0);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        // BS-APP-007 & ADR-0003: Under NO circumstances terminate or intervene against protected targets
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
        Assert.True(result.IsProtectedTarget);
        Assert.Equal(InterventionStatus.ProtectedTargetBypass, result.Status);
    }

    [Fact]
    public async Task StrictProfile_NeverClosesOrMinimizesBlockedApplication()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock, profileCode: BuiltInProfiles.StrictCode);

        var blockedApp = BlockedApp.Create(profileId, "Game", null, "game.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "game",
            ExecutablePath = @"C:\Games\game.exe",
            CloseRequestSuccess = true
        };

        var options = new BlockingEngineOptions
        {
            EnableStrictCloseRequest = true
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow,
            options);

        engine.StartMonitoring(session.Id);

        _ = await engine.EvaluateForegroundAppAsync(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(4);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.OverlayRequired, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(InterventionType.None, result.InterventionType);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Null(result.FailureReason);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task StrictProfile_DoesNotUseProcessActionAsOverlayFallback()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock, profileCode: BuiltInProfiles.StrictCode);

        var blockedApp = BlockedApp.Create(profileId, "Game", null, "game.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "game",
            ExecutablePath = @"C:\Games\game.exe",
            CloseRequestSuccess = false, // Close request fails!
            MinimizeSuccess = true       // Fallback to minimize succeeds
        };

        var options = new BlockingEngineOptions
        {
            EnableStrictCloseRequest = true
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow,
            options);

        engine.StartMonitoring(session.Id);

        _ = await engine.EvaluateForegroundAppAsync(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(4);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.OverlayRequired, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(InterventionType.None, result.InterventionType);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task OverlayRequest_DoesNotRecordFailureOrReportBlockedBeforeDesktopAcknowledgement()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe",
            MinimizeSuccess = false // Minimize fails!
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        _ = await engine.EvaluateForegroundAppAsync(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(4);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        db.Clock.UtcNow = TestStartTime.AddSeconds(5);
        var repeatedResult = await engine.EvaluateForegroundAppAsync(session.Id);

        // Detection requests the Desktop overlay but is not itself a success or failure.
        Assert.Equal(InterventionStatus.OverlayRequired, result.Status);
        Assert.False(result.Intervened);
        Assert.Null(result.FailureReason);
        Assert.Equal(InterventionStatus.OverlayRequired, repeatedResult.Status);
        Assert.False(repeatedResult.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);

        // Enforcement is only recorded after the Desktop reports actual overlay failure.
        var events = await repos.SessionEvents.ListBySessionAsync(SessionType.Focus, session.Id);
        Assert.DoesNotContain(events, e => e.EventType is SessionEventType.EnforcementFailed or SessionEventType.Blocked);
    }

    [Fact]
    public async Task Lifecycle_WhenSessionCompleted_StopsMonitoringImmediately()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);
        Assert.True(engine.IsMonitoring);

        // Complete the session
        session.Complete(db.Clock.UtcNow.AddMinutes(20), 1200, 1200, 0, 0);
        await repos.Context.SaveChangesAsync();

        // Evaluate should detect terminal session, stop monitoring, and return NoActiveSession
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NoActiveSession, result.Status);
        Assert.False(result.Intervened);
        Assert.False(engine.IsMonitoring);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task Lifecycle_WhenSessionAbandonedOrEndedEarly_StopsMonitoringImmediately()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        engine.StartMonitoring(session.Id);

        // End early
        session.EndEarly(db.Clock.UtcNow.AddMinutes(5), 300, 300, 0, 0, FocusEndReason.UserEnded);
        await repos.Context.SaveChangesAsync();

        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NoActiveSession, result.Status);
        Assert.False(result.Intervened);
        Assert.False(engine.IsMonitoring);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
    }

    [Fact]
    public async Task InterventionOccurred_EventRequestsOverlayWithoutClaimingItIsVisible()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock);

        var blockedApp = BlockedApp.Create(profileId, "Discord", null, "discord.exe", db.Clock.UtcNow);
        await repos.BlockedApps.AddAsync(blockedApp);
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop
        {
            ProcessName = "discord",
            ExecutablePath = @"C:\Users\test\AppData\Local\Discord\Discord.exe"
        };

        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions,
            repos.BlockedApps,
            repos.SessionExceptions,
            repos.FocusProfiles,
            db.Clock,
            repos.SessionEvents,
            fakeWindow);

        BlockingInterventionResult? capturedResult = null;
        engine.InterventionOccurred += (_, res) => capturedResult = res;

        engine.StartMonitoring(session.Id);

        _ = await engine.EvaluateForegroundAppAsync(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(4);
        _ = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.NotNull(capturedResult);
        Assert.Equal(InterventionStatus.OverlayRequired, capturedResult.Status);
        Assert.False(capturedResult.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
    }

    [Fact]
    public async Task FlexibleProfile_DoesNotEnforceConfiguredApplicationRules()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var repos = db.Store.CreateRepositories();
        var (session, profileId) = await SetupActiveSessionAsync(repos, db.Clock, profileCode: BuiltInProfiles.FlexibleCode);

        await repos.BlockedApps.AddAsync(BlockedApp.Create(profileId, "Game", null, "game.exe", db.Clock.UtcNow));
        await repos.Context.SaveChangesAsync();

        var fakeWindow = new FakeWindowInterop { ProcessName = "game", ExecutablePath = @"C:\Games\game.exe" };
        using var engine = new WindowsApplicationBlockingEngine(
            repos.FocusSessions, repos.BlockedApps, repos.SessionExceptions, repos.FocusProfiles,
            db.Clock, repos.SessionEvents, fakeWindow);

        engine.StartMonitoring(session.Id);
        db.Clock.UtcNow = TestStartTime.AddSeconds(10);
        var result = await engine.EvaluateForegroundAppAsync(session.Id);

        Assert.Equal(InterventionStatus.NotBlocked, result.Status);
        Assert.False(result.Intervened);
        Assert.Equal(0, fakeWindow.MinimizeCallCount);
        Assert.Equal(0, fakeWindow.CloseRequestCallCount);
        Assert.Empty(await repos.SessionEvents.ListBySessionAsync(SessionType.Focus, session.Id));
    }

    private static async Task<(FocusSession Session, Guid ProfileId)> SetupActiveSessionAsync(
        SqliteRepositorySet repos,
        TestLocalClock clock,
        string profileCode = BuiltInProfiles.BalancedCode)
    {
        var localProfile = await repos.LocalProfiles.FindSingleAsync();
        Assert.NotNull(localProfile);

        clock.UtcNow = TestStartTime;

        var session = FocusSession.Start(
            localProfile.Id,
            profileCode,
            1200,
            TestStartTime,
            EconomyRules.Version);

        await repos.FocusSessions.AddAsync(session);
        await repos.Context.SaveChangesAsync();

        return (session, localProfile.Id);
    }
}
