using BoundlessStudy.Application.Results;
using BoundlessStudy.Application.UseCases;

namespace BoundlessStudy.Application.Tests;

/// <summary>
/// The shared command boundary: one begin, commit only on success, rollback on
/// failed results, exceptions, and cancellation (A-02).
/// </summary>
public class CommandBoundaryTests
{
    [Fact]
    public async Task Successful_command_commits_once()
    {
        var unitOfWork = new FakeUnitOfWork();

        var result = await CommandBoundary.ExecuteAsync<UseCaseResult>(
            unitOfWork,
            () => UseCaseResult.Ok());

        Assert.True(result.Success);
        Assert.Equal(1, unitOfWork.BeginCount);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.Equal(0, unitOfWork.RollbackCount);
    }

    [Fact]
    public async Task Failed_command_rolls_back_and_does_not_commit()
    {
        var unitOfWork = new FakeUnitOfWork();

        var result = await CommandBoundary.ExecuteAsync<UseCaseResult>(
            unitOfWork,
            () => UseCaseResult.Fail("TEST_FAILURE", "expected failure"));

        Assert.False(result.Success);
        Assert.Equal(1, unitOfWork.BeginCount);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.Equal(1, unitOfWork.RollbackCount);
    }

    [Fact]
    public async Task Exception_rolls_back_and_is_rethrown()
    {
        var unitOfWork = new FakeUnitOfWork();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommandBoundary.ExecuteAsync<UseCaseResult>(
                unitOfWork,
                () => throw new InvalidOperationException("simulated failure")));

        Assert.Equal(1, unitOfWork.BeginCount);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.Equal(1, unitOfWork.RollbackCount);
    }

    [Fact]
    public async Task Cancellation_rolls_back_the_active_transaction()
    {
        var unitOfWork = new FakeUnitOfWork();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CommandBoundary.ExecuteAsync(
                unitOfWork,
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(UseCaseResult.Ok());
                },
                cancellation.Token));

        Assert.Equal(1, unitOfWork.BeginCount);
        Assert.Equal(0, unitOfWork.CommitCount);
        Assert.Equal(1, unitOfWork.RollbackCount);
    }

    [Fact]
    public async Task Dispatcher_uses_the_same_boundary()
    {
        var unitOfWork = new FakeUnitOfWork();
        var dispatcher = new CommandDispatcher(unitOfWork);

        var result = await dispatcher.ExecuteAsync<UseCaseResult>(
            _ => Task.FromResult(UseCaseResult.Ok()));

        Assert.True(result.Success);
        Assert.Equal(1, unitOfWork.BeginCount);
        Assert.Equal(1, unitOfWork.CommitCount);
        Assert.Equal(0, unitOfWork.RollbackCount);
    }
}
