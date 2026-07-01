using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.SharedKernel.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfTenantConfigurationRepository : ITenantConfigurationRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfTenantConfigurationRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        await _dbContext.Tenants.AddAsync(tenant, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<Tenant?> GetByIdAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        return await IncludeConfiguration(_dbContext.Tenants)
            .FirstOrDefaultAsync(x => x.Id == tenantId, cancellationToken);
    }

    public async Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken)
    {
        return await IncludeConfiguration(_dbContext.Tenants)
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        if (_dbContext.Entry(tenant).State == EntityState.Detached)
        {
            _dbContext.Tenants.Attach(tenant);
        }

        _dbContext.ChangeTracker.DetectChanges();
        await MarkNewAggregateChildrenAsAddedAsync(tenant, cancellationToken);

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    private async Task MarkNewAggregateChildrenAsAddedAsync(
        Tenant tenant,
        CancellationToken cancellationToken)
    {
        foreach (var sourceConnection in tenant.SourceConnections)
        {
            await MarkAsAddedIfMissingAsync(_dbContext.SourceConnections, sourceConnection, cancellationToken);
        }

        foreach (var webhookConfiguration in tenant.WebhookConfigurations)
        {
            await MarkAsAddedIfMissingAsync(_dbContext.WebhookConfigurations, webhookConfiguration, cancellationToken);
        }

        foreach (var destinationConfiguration in tenant.DestinationConfigurations)
        {
            await MarkAsAddedIfMissingAsync(_dbContext.DestinationConfigurations, destinationConfiguration, cancellationToken);
        }

        foreach (var mappingProfile in tenant.MappingProfiles)
        {
            await MarkAsAddedIfMissingAsync(_dbContext.MappingProfiles, mappingProfile, cancellationToken);
        }

        foreach (var route in tenant.ResourcePipelineRoutes)
        {
            await MarkAsAddedIfMissingAsync(_dbContext.ResourcePipelineRoutes, route, cancellationToken);
        }
    }

    private async Task MarkAsAddedIfMissingAsync<TEntity>(
        DbSet<TEntity> dbSet,
        TEntity entity,
        CancellationToken cancellationToken)
        where TEntity : Entity<Guid>
    {
        var entry = _dbContext.Entry(entity);
        if (entry.State == EntityState.Added)
        {
            return;
        }

        var exists = await dbSet
            .AsNoTracking()
            .AnyAsync(x => x.Id == entity.Id, cancellationToken);

        if (!exists)
        {
            entry.State = EntityState.Added;
        }
    }

    private static IQueryable<Tenant> IncludeConfiguration(IQueryable<Tenant> query)
    {
        return query
            .Include(x => x.SourceConnections)
            .Include(x => x.WebhookConfigurations)
            .Include(x => x.DestinationConfigurations)
            .Include(x => x.MappingProfiles)
            .Include(x => x.ResourcePipelineRoutes);
    }
}
