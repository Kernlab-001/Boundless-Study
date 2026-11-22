using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// The single command-dispatch transaction entry point. The agent host will resolve
/// each mutating command through this class; handlers may also use
/// <see cref="CommandBoundary"/> directly so a command is correct when tested or
/// called in isolation. Both share the same <see cref="IUnitOfWork"/> instance, so
/// an outer dispatch boundary and an inner handler boundary compose as one nested
/// transaction rather than two independently committed writes.
/// </summary>
public sealed class CommandDispatcher
{
    private readonly IUnitOfWork _unitOfWork;

    public CommandDispatcher(IUnitOfWork unitOfWork)
    {
        _unitOfWork = unitOfWork;
    }

    public Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> command,
        CancellationToken cancellationToken = default)
        where T : ICommandOutcome =>
        CommandBoundary.ExecuteAsync(_unitOfWork, command, cancellationToken);
}
