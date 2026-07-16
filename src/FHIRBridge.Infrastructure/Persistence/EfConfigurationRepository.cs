using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IConfigurationRepository"/>. Add* inserts the entity and saves; Update*
/// simply saves because the entity returned by a preceding Get* is tracked (change-tracked entities are updated by
/// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> without an explicit Update call).
/// </summary>
public sealed class EfConfigurationRepository : IConfigurationRepository
{
    private readonly FHIRBridgeDbContext _db;

    public EfConfigurationRepository(FHIRBridgeDbContext db)
    {
        _db = db;
    }

    // ── Source connections ────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SourceConnection>> GetSourceConnectionsAsync(CancellationToken ct) =>
        await _db.SourceConnections.OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<SourceConnection?> GetSourceConnectionAsync(Guid id, CancellationToken ct) =>
        await _db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddSourceConnectionAsync(SourceConnection e, CancellationToken ct)
    {
        await _db.SourceConnections.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task UpdateSourceConnectionAsync(SourceConnection e, CancellationToken ct) =>
        _db.SaveChangesAsync(ct);

    public Task DeleteSourceConnectionAsync(SourceConnection sourceConnection, CancellationToken cancellationToken)
    {
        // SourceConnection is ISoftDeletable: AuditingSaveChangesInterceptor converts this Remove into a soft
        // delete (IsDeleted/DeletedBy/DeletedOnUtc) rather than issuing a physical DELETE.
        _db.SourceConnections.Remove(sourceConnection);
        return _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> ExistsWithNameAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var query = _db.SourceConnections.Where(x => x.Name.ToUpper() == name.ToUpper());
        if (excludeId is { } id)
        {
            query = query.Where(x => x.Id != id);
        }

        return await query.AnyAsync(cancellationToken);
    }

    // ── Destinations ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct) =>
        await _db.DestinationConfigurations.OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<PagedResult<DestinationConfiguration>> GetDestinationsPagedAsync(
        DestinationFilter filter,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _db.DestinationConfigurations.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search;
            query = query.Where(x => EF.Functions.Like(x.Name, $"%{search}%"));
        }

        if (filter.DestinationType.HasValue)
        {
            query = query.Where(x => x.DestinationType == filter.DestinationType.Value);
        }

        if (filter.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == filter.IsEnabled.Value);
        }

        var totalCount = await query.CountAsync(ct);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var items = await query
            .OrderBy(x => x.Name)
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new PagedResult<DestinationConfiguration>(items, totalCount, page, take);
    }

    public async Task<DestinationConfiguration?> GetDestinationAsync(Guid id, CancellationToken ct) =>
        await _db.DestinationConfigurations.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddDestinationAsync(DestinationConfiguration e, CancellationToken ct)
    {
        await _db.DestinationConfigurations.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task UpdateDestinationAsync(DestinationConfiguration e, CancellationToken ct) =>
        _db.SaveChangesAsync(ct);

    public Task RemoveDestinationAsync(DestinationConfiguration e, CancellationToken ct)
    {
        _db.DestinationConfigurations.Remove(e);
        return _db.SaveChangesAsync(ct);
    }

    public async Task<bool> HasDestinationExecutionHistoryAsync(Guid destinationId, CancellationToken ct)
    {
        var mappingIds = await _db.MappingProfiles
            .Where(m => m.DestinationId == destinationId)
            .Select(m => m.Id)
            .ToListAsync(ct);

        if (mappingIds.Count == 0)
        {
            return false;
        }

        return await _db.PipelineRunRouteExecutions.AnyAsync(e => mappingIds.Contains(e.MappingProfileId), ct);
    }

    // ── Mapping profiles ──────────────────────────────────────────────────────
    public async Task<IReadOnlyList<MappingProfile>> GetMappingProfilesAsync(CancellationToken ct) =>
        await _db.MappingProfiles.Include(x => x.Fields).OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<MappingProfile?> GetMappingProfileAsync(Guid id, CancellationToken ct) =>
        await _db.MappingProfiles.Include(x => x.Fields).FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddMappingProfileAsync(MappingProfile e, CancellationToken ct)
    {
        await _db.MappingProfiles.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task UpdateMappingProfileAsync(MappingProfile e, CancellationToken ct) =>
        _db.SaveChangesAsync(ct);

    // ── Resource pipeline routes ──────────────────────────────────────────────
    public async Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesAsync(CancellationToken ct) =>
        await _db.ResourcePipelineRoutes
            .Include(x => x.ResourceMappings)
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

    public async Task<ResourcePipelineRoute?> GetRouteAsync(Guid id, CancellationToken ct) =>
        await _db.ResourcePipelineRoutes
            .Include(x => x.ResourceMappings)
            .FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddRouteAsync(ResourcePipelineRoute e, CancellationToken ct)
    {
        await _db.ResourcePipelineRoutes.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task UpdateRouteAsync(ResourcePipelineRoute e, CancellationToken ct) =>
        _db.SaveChangesAsync(ct);

    // ── Webhooks ──────────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<WebhookConfiguration>> GetWebhooksAsync(CancellationToken ct) =>
        await _db.WebhookConfigurations.OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<WebhookConfiguration?> GetWebhookAsync(Guid id, CancellationToken ct) =>
        await _db.WebhookConfigurations.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddWebhookAsync(WebhookConfiguration e, CancellationToken ct)
    {
        await _db.WebhookConfigurations.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public Task UpdateWebhookAsync(WebhookConfiguration e, CancellationToken ct) =>
        _db.SaveChangesAsync(ct);

    // ── EHR endpoints ─────────────────────────────────────────────────────────
    public async Task<EhrEndpoint?> GetEhrEndpointAsync(Guid id, CancellationToken ct) =>
        await _db.EhrEndpoints.FirstOrDefaultAsync(x => x.Id == id, ct);
}
