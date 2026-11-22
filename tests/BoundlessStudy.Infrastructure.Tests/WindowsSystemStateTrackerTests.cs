using BoundlessStudy.Application.Services;
using BoundlessStudy.Infrastructure.Services;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class WindowsSystemStateTrackerTests
{
    [Fact]
    public void Tracks_lock_sleep_and_idle_transitions()
    {
        using var tracker = new WindowsSystemStateTracker(new TestLocalClock())
        {
            IdleThreshold = TimeSpan.FromMinutes(5)
        };

        bool? locked = null;
        bool? suspended = null;
        bool? idle = null;
        tracker.WorkstationLockChanged += (_, e) => locked = e.IsLocked;
        tracker.PowerSuspendChanged += (_, e) => suspended = e.IsSuspended;
        tracker.IdleStateChanged += (_, e) => idle = e.IsIdle;

        tracker.HandleSessionSwitch(WindowsSystemStateTracker.WTS_SESSION_LOCK);
        Assert.True(tracker.IsWorkstationLocked);
        Assert.True(locked);
        tracker.HandleSessionSwitch(WindowsSystemStateTracker.WTS_SESSION_UNLOCK);
        Assert.False(tracker.IsWorkstationLocked);
        Assert.False(locked);

        tracker.HandlePowerBroadcast(WindowsSystemStateTracker.PBT_APMSUSPEND);
        Assert.True(tracker.IsSuspended);
        Assert.True(suspended);
        tracker.HandlePowerBroadcast(WindowsSystemStateTracker.PBT_APMRESUMEAUTOMATIC);
        Assert.False(tracker.IsSuspended);
        Assert.False(suspended);

        tracker.SetIdleDurationOverride(TimeSpan.FromMinutes(6));
        Assert.True(tracker.IsIdle);
        Assert.True(idle);
        tracker.SetIdleDurationOverride(TimeSpan.FromMinutes(2));
        Assert.False(tracker.IsIdle);
        Assert.False(idle);
    }

    [Fact]
    public void Rejects_invalid_idle_timing_configuration()
    {
        using var tracker = new WindowsSystemStateTracker();
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.IdleThreshold = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.IdleThreshold = TimeSpan.FromSeconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.IdlePollInterval = TimeSpan.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => tracker.IdlePollInterval = TimeSpan.FromMilliseconds(-1));
    }

    [Fact]
    public void Emits_only_one_timestamped_system_ending_signal()
    {
        using var tracker = new WindowsSystemStateTracker();
        var observed = new List<SystemEndingEventArgs>();
        tracker.SystemEnding += (_, e) => observed.Add(e);
        var sourceTime = new DateTime(2026, 11, 22, 10, 15, 0, DateTimeKind.Unspecified);

        tracker.HandleSessionEnding(SystemEndingKind.Logoff, sourceTime);
        tracker.HandleSessionEnding(SystemEndingKind.Logoff, sourceTime.AddSeconds(1));
        tracker.HandleSessionEnding(SystemEndingKind.Shutdown, sourceTime.AddSeconds(2));

        var ending = Assert.Single(observed);
        Assert.Equal(SystemEndingKind.Logoff, ending.Reason);
        Assert.Equal(DateTimeKind.Utc, ending.OccurredAtUtc.Kind);
        Assert.Equal(DateTime.SpecifyKind(sourceTime, DateTimeKind.Utc), ending.OccurredAtUtc);
    }
}
