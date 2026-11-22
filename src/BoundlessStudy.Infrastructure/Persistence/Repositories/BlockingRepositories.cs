using BoundlessStudy.Application.Abstractions;
using BoundlessStudy.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace BoundlessStudy.Infrastructure.Persistence.Repositories;

/// <summary>SQLite implementation of <see cref="IBlockedAppRepository"/>.</summary>
public sealed class BlockedAppRepository : IBlockedAppRepository
{
    private readonly BoundlessDbContext _context;

    public BlockedAppRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<BlockedApp?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.BlockedApps.SingleOrDefaultAsync(a => a.Id == id, cancellationToken);

    public async Task<IReadOnlyList<BlockedApp>> ListAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BlockedApps
            .Where(a => a.LocalProfileId == localProfileId)
            .OrderBy(a => a.DisplayName)
            .ThenBy(a => a.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountEnabledAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        _context.BlockedApps.CountAsync(a => a.LocalProfileId == localProfileId && a.IsEnabled, cancellationToken);

    public async Task AddAsync(BlockedApp app, CancellationToken cancellationToken = default)
    {
        await _context.BlockedApps.AddAsync(app, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(BlockedApp app, CancellationToken cancellationToken = default)
    {
        _context.BlockedApps.Remove(app);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IBlockedSiteRepository"/>.</summary>
public sealed class BlockedSiteRepository : IBlockedSiteRepository
{
    private readonly BoundlessDbContext _context;

    public BlockedSiteRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<BlockedSite?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.BlockedSites.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<BlockedSite?> FindByDomainAsync(
        Guid localProfileId,
        string normalizedDomain,
        CancellationToken cancellationToken = default) =>
        _context.BlockedSites.SingleOrDefaultAsync(
            s => s.LocalProfileId == localProfileId && s.NormalizedDomain == normalizedDomain,
            cancellationToken);

    public async Task<IReadOnlyList<BlockedSite>> ListAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.BlockedSites
            .Where(s => s.LocalProfileId == localProfileId)
            .OrderBy(s => s.NormalizedDomain)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    public Task<int> CountEnabledAsync(Guid localProfileId, CancellationToken cancellationToken = default) =>
        _context.BlockedSites.CountAsync(s => s.LocalProfileId == localProfileId && s.IsEnabled, cancellationToken);

    public async Task AddAsync(BlockedSite site, CancellationToken cancellationToken = default)
    {
        await _context.BlockedSites.AddAsync(site, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(BlockedSite site, CancellationToken cancellationToken = default)
    {
        _context.BlockedSites.Remove(site);
        await _context.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>SQLite implementation of <see cref="IAllowedSiteRepository"/>.</summary>
public sealed class AllowedSiteRepository : IAllowedSiteRepository
{
    private readonly BoundlessDbContext _context;

    public AllowedSiteRepository(BoundlessDbContext context)
    {
        _context = context;
    }

    public Task<AllowedSite?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        _context.AllowedSites.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);

    public Task<AllowedSite?> FindByDomainAsync(
        Guid localProfileId,
        string normalizedDomain,
        CancellationToken cancellationToken = default) =>
        _context.AllowedSites.SingleOrDefaultAsync(
            s => s.LocalProfileId == localProfileId && s.NormalizedDomain == normalizedDomain,
            cancellationToken);

    public async Task<IReadOnlyList<AllowedSite>> ListAsync(
        Guid localProfileId,
        CancellationToken cancellationToken = default)
    {
        return await _context.AllowedSites
            .Where(s => s.LocalProfileId == localProfileId)
            .OrderBy(s => s.NormalizedDomain)
            .ThenBy(s => s.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task AddAsync(AllowedSite site, CancellationToken cancellationToken = default)
    {
        await _context.AllowedSites.AddAsync(site, cancellationToken);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(AllowedSite site, CancellationToken cancellationToken = default)
    {
        _context.AllowedSites.Remove(site);
        await _context.SaveChangesAsync(cancellationToken);
    }
}
