using BoundlessStudy.Domain;

namespace BoundlessStudy.Application.Tests;

public sealed class CheckpointStateTests
{
    [Fact]
    public async Task RecoveryCheckpoint_IsAcceptedForRecoverableSession()
    {
        var harness = new TestHarness();
        var sessionId = harness.StartSessionAndGetId("Balanced", plannedMinutes: 25);
        await harness.Interrupt.HandleAsync(sessionId, FocusEndReason.AgentCrash);

        var result = await harness.Checkpoint.HandleAsync(
            sessionId,
            elapsedSeconds: 90,
            activeStudySeconds: 90,
            idleSeconds: 0,
            protectionGapSeconds: 0,
            violationCount: 0,
            exceptionCount: 0,
            recoveryCount: 0);

        var stored = await harness.Sessions.FindAsync(sessionId);
        Assert.True(result.Success);
        Assert.NotNull(stored);
        Assert.Equal(SessionState.RecoverableInterrupted, stored.State);
        Assert.Equal(90, stored.ElapsedSeconds);
        Assert.Equal(90, stored.ActiveStudySeconds);
    }
}
