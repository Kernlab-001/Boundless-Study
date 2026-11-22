using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// One shared transaction boundary for a mutating application command. The command
/// begins before it changes state, commits only when it reports success, and rolls
/// back on a failed result, an exception, or cancellation. Nested calls share the
/// outer transaction through <see cref="IUnitOfWork"/>; read-only queries never use
/// this boundary. The future agent command dispatcher must call commands through
/// <see cref="CommandDispatcher"/> so composition across handlers stays atomic.
/// </summary>
public static class CommandBoundary
{
    public static async Task<T> ExecuteAsync<T>(
        IUnitOfWork unitOfWork,
        Func<CancellationToken, Task<T>> command,
        CancellationToken cancellationToken = default)
        where T : ICommandOutcome
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(command);

        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            var result = await command(cancellationToken);
            if (result.Success)
            {
                await unitOfWork.CommitAsync(cancellationToken);
            }
            else
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
            }

            return result;
        }
        catch
        {
            // A cancelled command must still release its write transaction.
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>Synchronous-overload for handlers whose mutation needs no await.</summary>
    public static Task<T> ExecuteAsync<T>(
        IUnitOfWork unitOfWork,
        Func<T> command,
        CancellationToken cancellationToken = default)
        where T : ICommandOutcome =>
        ExecuteAsync(unitOfWork, _ => Task.FromResult(command()), cancellationToken);
}
