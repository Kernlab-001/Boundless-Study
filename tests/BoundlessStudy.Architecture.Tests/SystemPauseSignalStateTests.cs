using BoundlessStudy.Agent;

namespace BoundlessStudy.Architecture.Tests;

public sealed class SystemPauseSignalStateTests
{
    [Fact]
    public void OverlappingLockAndSleep_DoNotResumeUntilBothSignalsClear()
    {
        var state = new SystemPauseSignalState();
        var t0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(state.SetLocked(true, t0.AddSeconds(1)));
        Assert.Null(state.SetSuspended(true, t0.AddSeconds(2)));
        Assert.Null(state.SetSuspended(false, t0.AddSeconds(3)));
        Assert.True(state.IsPaused);
        Assert.False(state.SetLocked(false, t0.AddSeconds(4)));
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void DelayedOrDuplicateSignals_DoNotReverseCurrentPauseState()
    {
        var state = new SystemPauseSignalState();
        var t0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(state.SetLocked(true, t0.AddSeconds(2)));
        Assert.Null(state.SetLocked(false, t0.AddSeconds(1)));
        Assert.Null(state.SetLocked(true, t0.AddSeconds(2)));
        Assert.True(state.IsPaused);
        Assert.False(state.SetLocked(false, t0.AddSeconds(3)));
    }

    [Fact]
    public void LocalAndUnspecifiedSignalTimes_AreNormalizedToUtcForOrdering()
    {
        var state = new SystemPauseSignalState();
        var utc = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.True(state.SetSuspended(true, unspecified));
        Assert.Null(state.SetSuspended(false, utc));
        Assert.True(state.IsPaused);
    }

    [Fact]
    public void ApplicationEnforcementFailure_SharesPauseWithLockAndResumesOnlyAfterBothClear()
    {
        var state = new SystemPauseSignalState();
        var t0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var sessionId = Guid.NewGuid();

        Assert.True(state.SetApplicationProtectionFailure(sessionId, true, t0.AddSeconds(1)));
        Assert.Null(state.SetLocked(true, t0.AddSeconds(2)));
        Assert.Null(state.SetApplicationProtectionFailure(sessionId, false, t0.AddSeconds(3)));
        Assert.True(state.IsPaused);
        Assert.False(state.SetLocked(false, t0.AddSeconds(4)));
        Assert.False(state.IsPaused);
    }

    [Fact]
    public void ApplicationFailureForNewSession_DoesNotLeakPriorSessionPause()
    {
        var state = new SystemPauseSignalState();
        var t0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);

        Assert.True(state.SetApplicationProtectionFailure(Guid.NewGuid(), true, t0.AddSeconds(1)));
        Assert.False(state.SetApplicationProtectionFailure(Guid.NewGuid(), false, t0.AddSeconds(2)));
        Assert.False(state.IsPaused);
    }
}
