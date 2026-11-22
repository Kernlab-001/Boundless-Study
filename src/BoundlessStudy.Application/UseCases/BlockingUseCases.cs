using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Application.Results;
using BoundlessStudy.Domain;
using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Application.UseCases;

/// <summary>
/// Adds a blocked application rule (§7.1). The domain rejects system-critical,
/// accessibility, and product-owned targets; the handler rejects duplicates
/// (discouraged; merged by the UI).
/// </summary>
public sealed class AddBlockedAppHandler
{
    private readonly IBlockedAppRepository _apps;
    private readonly ILocalProfileRepository _profiles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public AddBlockedAppHandler(
        IBlockedAppRepository apps,
        ILocalProfileRepository profiles,
        IUnitOfWork unitOfWork,
        ILocalClock clock)
    {
        _apps = apps;
        _profiles = profiles;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<EntityResult> HandleAsync(
        Guid localProfileId,
        string displayName,
        string? executablePath,
        string? executableName,
        CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        try
        {
            var target = Domain.ValueObjects.ExecutableTarget.Create(executablePath, executableName);
            var existing = await _apps.ListAsync(localProfileId, cancellationToken);
            var duplicate = existing.FirstOrDefault(a =>
                string.Equals(a.NormalizedExecutableName, target.NormalizedExecutableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.NormalizedExecutablePath ?? string.Empty, target.NormalizedExecutablePath ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
            {
                return EntityResult.Fail(UseCaseErrorCodes.DuplicateRule, "An identical blocked-application rule already exists.");
            }

            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var app = BlockedApp.Create(localProfileId, displayName, executablePath, executableName, _clock.UtcNow);
                    await _apps.AddAsync(app, ct);
                    return EntityResult.Ok(app.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>Adds a blocked website rule (§7.2); the normalized domain is unique per profile.</summary>
public sealed class AddBlockedSiteHandler
{
    private readonly IBlockedSiteRepository _sites;
    private readonly ILocalProfileRepository _profiles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public AddBlockedSiteHandler(
        IBlockedSiteRepository sites,
        ILocalProfileRepository profiles,
        IUnitOfWork unitOfWork,
        ILocalClock clock)
    {
        _sites = sites;
        _profiles = profiles;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<EntityResult> HandleAsync(Guid localProfileId, string domain, string? displayName, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        try
        {
            var normalized = Domain.ValueObjects.NormalizedDomain.Create(domain).Value;
            if (await _sites.FindByDomainAsync(localProfileId, normalized, cancellationToken) is not null)
            {
                return EntityResult.Fail(UseCaseErrorCodes.DuplicateRule, "This domain is already in the block list.");
            }

            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var site = BlockedSite.Create(localProfileId, domain, displayName, _clock.UtcNow);
                    await _sites.AddAsync(site, ct);
                    return EntityResult.Ok(site.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }
}

/// <summary>
/// Adds an allowed website rule (§4.11). A domain may exist in both lists; the UI
/// explains allow-over-block precedence rather than silently deduplicating.
/// </summary>
public sealed class AddAllowedSiteHandler
{
    private readonly IAllowedSiteRepository _sites;
    private readonly ILocalProfileRepository _profiles;
    private readonly IUnitOfWork _unitOfWork;
    private readonly ILocalClock _clock;

    public AddAllowedSiteHandler(
        IAllowedSiteRepository sites,
        ILocalProfileRepository profiles,
        IUnitOfWork unitOfWork,
        ILocalClock clock)
    {
        _sites = sites;
        _profiles = profiles;
        _unitOfWork = unitOfWork;
        _clock = clock;
    }

    public async Task<EntityResult> HandleAsync(Guid localProfileId, string domain, string? displayName, CancellationToken cancellationToken = default)
    {
        if (await _profiles.FindAsync(localProfileId, cancellationToken) is null)
        {
            return EntityResult.Fail(UseCaseErrorCodes.ProfileNotFound, "The local profile does not exist.");
        }

        try
        {
            var normalized = Domain.ValueObjects.NormalizedDomain.Create(domain).Value;
            if (await _sites.FindByDomainAsync(localProfileId, normalized, cancellationToken) is not null)
            {
                return EntityResult.Fail(UseCaseErrorCodes.DuplicateRule, "This domain is already in the allow list.");
            }

            return await CommandBoundary.ExecuteAsync(
                _unitOfWork,
                async ct =>
                {
                    var site = AllowedSite.Create(localProfileId, domain, displayName, _clock.UtcNow);
                    await _sites.AddAsync(site, ct);
                    return EntityResult.Ok(site.Id);
                },
                cancellationToken);
        }
        catch (DomainException ex)
        {
            return EntityResult.Fail(ex.Code, ex.Message);
        }
    }
}
