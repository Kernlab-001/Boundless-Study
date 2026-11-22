using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>Creates a subject (BS-STU-001). Duplicates are allowed; the UI warns.</summary>
public sealed class CreateSubjectHandler
{
    private readonly ILocalProfileRepository _profiles;
    private readonly ISubjectRepository _subjects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public CreateSubjectHandler(
        ILocalProfileRepository profiles,
        ISubjectRepository subjects,
        IUnitOfWork unitOfWork,
        ILocalClock clock)
    {
        _profiles = profiles;
        _subjects = subjects;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<EntityResult> HandleAsync(Guid localProfileId, string name, string? description, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var subject = Subject.Create(localProfileId, name, description, _clock.UtcNow);
                    await _subjects.AddAsync(subject, ct);
                    return EntityResult.Ok(subject.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Archives a subject; it disappears from new-task selection but history stays readable.</summary>
public sealed class ArchiveSubjectHandler
{
    private readonly ISubjectRepository _subjects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public ArchiveSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _subjects = subjects;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid subjectId, bool restore = false, CancellationToken cancellationToken = default)
    {
        var subject = await _subjects.FindAsync(subjectId, cancellationToken);
        if (subject is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SubjectNotFound, "The subject does not exist.");
        }

        return await CommandBoundary.ExecuteAsync(
            _unitOfWork,
            () =>
            {
                if (restore)
                {
                    subject.Restore(_clock.UtcNow);
                }
                else
                {
                    subject.Archive(_clock.UtcNow);
                }

                return UseCaseResult.Ok();
            },
            cancellationToken);
    }
}

/// <summary>Renames or re-describes a subject; existing session snapshots are never rewritten.</summary>
public sealed class UpdateSubjectHandler
{
    private readonly ISubjectRepository _subjects;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public UpdateSubjectHandler(ISubjectRepository subjects, IUnitOfWork unitOfWork, ILocalClock clock)
    {
        _subjects = subjects;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<UseCaseResult> HandleAsync(Guid subjectId, string name, string? description, CancellationToken cancellationToken = default)
    {
        var subject = await _subjects.FindAsync(subjectId, cancellationToken);
        if (subject is null)
        {
            return UseCaseResult.Fail(UseCaseErrorCodes.SubjectNotFound, "The subject does not exist.");
        }

        try
        {
            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                () =>
                {
                    subject.Rename(name, _clock.UtcNow);
                    subject.SetDescription(description, _clock.UtcNow);
                    return UseCaseResult.Ok();
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return UseCaseResult.Fail(ex.Code, ex.Message);
        }
    }
}
