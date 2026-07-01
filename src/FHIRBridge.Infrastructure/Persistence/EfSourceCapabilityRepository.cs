using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

public sealed class EfSourceCapabilityRepository : ISourceCapabilityRepository
{
    private readonly FHIRBridgeDbContext _dbContext;

    public EfSourceCapabilityRepository(FHIRBridgeDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<SourceCapabilityProfile?> GetBySourceConnectionIdAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        return _dbContext.SourceCapabilityProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.TenantId == tenantId && x.SourceConnectionId == sourceConnectionId,
                cancellationToken);
    }

    public async Task UpsertAsync(SourceCapabilityProfile profile, CancellationToken cancellationToken)
    {
        var existing = await _dbContext.SourceCapabilityProfiles
            .FirstOrDefaultAsync(
                x => x.TenantId == profile.TenantId && x.SourceConnectionId == profile.SourceConnectionId,
                cancellationToken);

        if (existing is null)
        {
            await _dbContext.SourceCapabilityProfiles.AddAsync(profile, cancellationToken);
        }
        else
        {
            existing.Replace(
                profile.FhirVersion,
                profile.Resources,
                profile.ConfiguredScopes,
                profile.RawCapabilityJson,
                profile.DiscoveredOnUtc);
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }
}
