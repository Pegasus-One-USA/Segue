using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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

    /// <summary>
    /// Recursively marks every owned reference navigation (OwnsOne, including nested OwnsOne-within-OwnsOne, e.g.
    /// SourceConnection.Authentication.ClientSecret) as <see cref="EntityState.Modified"/>. Call this before
    /// <see cref="DbContext.SaveChangesAsync(CancellationToken)"/> whenever a domain Update() method reassigns a
    /// brand-new owned-value-object instance onto an already-tracked entity — EF Core's automatic change detection
    /// does not reliably flag a nested owned entity's own properties as changed purely because its *parent* owned
    /// reference was replaced wholesale, so relying on it silently leaves nested columns at their old values even
    /// though SaveChangesAsync completes without error. A no-op (produces an identical UPDATE) when nothing nested
    /// actually changed, so this is safe to apply unconditionally.
    /// </summary>
    private static void MarkOwnedGraphModified(EntityEntry entry)
    {
        foreach (var reference in entry.References)
        {
            if (reference.TargetEntry is null)
            {
                continue;
            }

            reference.TargetEntry.State = EntityState.Modified;
            MarkOwnedGraphModified(reference.TargetEntry);
        }
    }

    public async Task<IConfigurationTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
    {
        var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        return new EfConfigurationTransaction(transaction);
    }

    private sealed class EfConfigurationTransaction : IConfigurationTransaction
    {
        private readonly Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction _transaction;

        public EfConfigurationTransaction(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction)
        {
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken cancellationToken) => _transaction.CommitAsync(cancellationToken);

        public Task RollbackAsync(CancellationToken cancellationToken) => _transaction.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }

    // ── Source connections ────────────────────────────────────────────────────
    public async Task<IReadOnlyList<SourceConnection>> GetSourceConnectionsAsync(CancellationToken ct) =>
        await _db.SourceConnections.OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<PagedResult<SourceConnection>> GetSourceConnectionsPagedAsync(
        SourceConnectionFilter filter,
        int page,
        int pageSize,
        string? sortBy,
        string? sortOrder,
        CancellationToken ct)
    {
        var query = _db.SourceConnections.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // Upper-cased on both sides rather than left to the database's own collation: SQL Server's default
            // collation is case-insensitive but PostgreSQL's LIKE is not, so an interpolated LIKE matched
            // nothing on Npgsql unless the user typed the stored casing exactly. Contains() (instead of an
            // interpolated LIKE pattern) also parameterizes the term, so a '%' or '_' the user types is matched
            // literally rather than acting as a wildcard.
            var search = filter.Search.ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(search) ||
                x.BaseUrl.ToUpper().Contains(search));
        }

        if (filter.SourceSystemType.HasValue)
        {
            query = query.Where(x => x.SourceSystemType == filter.SourceSystemType.Value);
        }

        if (filter.ApplicationType.HasValue)
        {
            query = query.Where(x => x.ApplicationType == filter.ApplicationType.Value);
        }

        if (filter.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == filter.IsEnabled.Value);
        }

        var totalCount = await query.CountAsync(ct);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy?.ToLowerInvariant() switch
        {
            // Must match the frontend's SourceSortColumn values (source-connection.model.ts) lowercased,
            // not the column display labels (EHR/Audience/Status) — those never matched here, so sorting
            // by those three columns silently no-opped back to Name ordering.
            "sourcesystemtype" => desc ? query.OrderByDescending(x => x.SourceSystemType)               : query.OrderBy(x => x.SourceSystemType),
            "applicationtype"  => desc ? query.OrderByDescending(x => x.ApplicationType)                : query.OrderBy(x => x.ApplicationType),
            "isenabled"        => desc ? query.OrderByDescending(x => x.IsEnabled)                       : query.OrderBy(x => x.IsEnabled),
            "actionon"         => desc ? query.OrderByDescending(x => x.ModifiedOnUtc ?? x.CreatedOnUtc) : query.OrderBy(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            _                  => desc ? query.OrderByDescending(x => x.Name)                            : query.OrderBy(x => x.Name),
        };

        var items = await query
            .Skip(skip) 
            .Take(take)
            .ToListAsync(ct);

        return new PagedResult<SourceConnection>(items, totalCount, page, take);
    }

    public async Task<SourceConnection?> GetSourceConnectionAsync(Guid id, CancellationToken ct) =>
        await _db.SourceConnections.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddSourceConnectionAsync(SourceConnection e, CancellationToken ct)
    {
        await _db.SourceConnections.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateSourceConnectionAsync(SourceConnection e, CancellationToken ct)
    {
        // Update() reassigns a brand-new Authentication instance, which itself owns nested ClientSecret/PrivateKey
        // (OwnsOne within OwnsOne — see SourceConnectionConfiguration). EF Core's automatic change detection does
        // not reliably propagate into a nested owned entity when its *parent* owned reference is replaced wholesale
        // (a documented EF Core limitation, not something fixable from the domain model without turning
        // SourceAuthenticationConfiguration into a mutable in-place-edited type) — the nested KeyVaultName/
        // SecretName columns silently kept their old values across every Update() in practice, even though a
        // secret was correctly written to the secret store every time (WriteInlineClientSecretAsync succeeded) and
        // sibling top-level properties like Scopes updated fine. Forcing the whole owned graph to Modified before
        // SaveChangesAsync guarantees every nested owned column is included in the UPDATE regardless of whether EF
        // detected the change itself.
        MarkOwnedGraphModified(_db.Entry(e));
        await _db.SaveChangesAsync(ct);

        // Update() reassigns brand-new owned-value-object instances (Authentication/Interactive/Retrieval, and
        // Authentication's own nested owned ClientSecret/PrivateKey) onto this tracked entity. EF Core's post-save
        // fixup for a *replaced* owned reference leaves orphaned entries for the old owned instances in the change
        // tracker — detaching just the root entity doesn't remove those nested orphans. A later Get*Async for the
        // same id within this same scoped DbContext (e.g. /workflows/build's Mappings step resolving a source it
        // just updated in the Sources step) resolves via the identity map into that corrupted state and
        // NullReferenceExceptions inside EF's navigation fixup. Clearing the whole tracker is the workaround EF
        // Core itself recommends for this scenario; every read after this point re-materializes fresh from the DB.
        _db.ChangeTracker.Clear();
    }

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

    // ── Source configurations ─────────────────────────────────────────────────
    public async Task<IReadOnlyList<SourceConfiguration>> GetSourceConfigurationsAsync(CancellationToken ct) =>
        await _db.SourceConfigurations.OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<SourceConfiguration?> GetSourceConfigurationAsync(Guid id, CancellationToken ct) =>
        await _db.SourceConfigurations.FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task AddSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct)
    {
        await _db.SourceConfigurations.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct)
    {
        // See UpdateSourceConnectionAsync's remarks — same owned-reference-replacement risk whenever Update()
        // reassigns a brand-new owned instance onto an already-tracked entity.
        MarkOwnedGraphModified(_db.Entry(e));
        await _db.SaveChangesAsync(ct);

        // Same owned-reference-replacement corruption as UpdateSourceConnectionAsync above (Update() reassigns a
        // brand-new owned Retrieval instance) — clear the tracker so a later Get*Async for this id within the same
        // request's DbContext re-materializes cleanly instead of hitting the corrupted tracked entry/orphans.
        _db.ChangeTracker.Clear();
    }

    public Task DeleteSourceConfigurationAsync(SourceConfiguration sourceConfiguration, CancellationToken cancellationToken)
    {
        // SourceConfiguration is ISoftDeletable: AuditingSaveChangesInterceptor converts this Remove into a soft
        // delete rather than issuing a physical DELETE.
        _db.SourceConfigurations.Remove(sourceConfiguration);
        return _db.SaveChangesAsync(cancellationToken);
    }

    // ── Destinations ──────────────────────────────────────────────────────────
    public async Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct) =>
        await _db.DestinationConfigurations.OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<PagedResult<DestinationConfiguration>> GetDestinationsPagedAsync(
        DestinationFilter filter,
        int page,
        int pageSize,
        string? sortBy,
        string? sortOrder,
        CancellationToken ct)
    {
        var query = _db.DestinationConfigurations.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // See GetSourceConnectionsPagedAsync for why this upper-cases both sides instead of relying on the
            // database collation, and why it uses Contains() rather than an interpolated LIKE pattern.
            var search = filter.Search.ToUpperInvariant();
            query = query.Where(x => x.Name.ToUpper().Contains(search));
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

        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy?.ToLowerInvariant() switch
        {
            "type"     => desc ? query.OrderByDescending(x => x.DestinationType)                  : query.OrderBy(x => x.DestinationType),
            "target"   => desc ? query.OrderByDescending(x => x.Target)                             : query.OrderBy(x => x.Target),
            "status"   => desc ? query.OrderByDescending(x => x.IsEnabled)                          : query.OrderBy(x => x.IsEnabled),
            "actionon" => desc ? query.OrderByDescending(x => x.ModifiedOnUtc ?? x.CreatedOnUtc)    : query.OrderBy(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            _          => desc ? query.OrderByDescending(x => x.Name)                              : query.OrderBy(x => x.Name),
        };

        var items = await query
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

    public async Task UpdateDestinationAsync(DestinationConfiguration e, CancellationToken ct)
    {
        // See UpdateSourceConnectionAsync's remarks — same owned-reference-replacement risk whenever Update()
        // reassigns a brand-new owned SecretReference onto an already-tracked entity. Rotating a destination's
        // secret currently happens to work in practice mostly because every observed test workflow created a
        // fresh destination rather than editing an existing one's secret — the underlying risk is identical.
        MarkOwnedGraphModified(_db.Entry(e));
        await _db.SaveChangesAsync(ct);

        // Same owned-reference-replacement corruption as UpdateSourceConnectionAsync above (Update() reassigns a
        // brand-new owned SecretReference) — clear the tracker so a later Get*Async for this id within the same
        // request's DbContext re-materializes cleanly instead of hitting the corrupted tracked entry/orphans.
        _db.ChangeTracker.Clear();
    }

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

    public async Task<PagedResult<MappingProfile>> GetMappingProfilesPagedAsync(
        MappingProfileFilter filter,
        int page,
        int pageSize,
        string? sortBy,
        string? sortOrder,
        CancellationToken ct)
    {
        var query = _db.MappingProfiles.Include(x => x.Fields).AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            // See GetSourceConnectionsPagedAsync for why this upper-cases both sides instead of relying on the
            // database collation, and why it uses Contains() rather than an interpolated LIKE pattern.
            var search = filter.Search.ToUpperInvariant();
            query = query.Where(x =>
                x.Name.ToUpper().Contains(search) ||
                x.DestinationObject.ToUpper().Contains(search));
        }

        if (!string.IsNullOrWhiteSpace(filter.ResourceType))
        {
            query = query.Where(x => x.ResourceType == filter.ResourceType);
        }

        if (filter.SourceConnectionId.HasValue)
        {
            query = query.Where(x => x.SourceConnectionId == filter.SourceConnectionId.Value);
        }

        if (filter.DestinationId.HasValue)
        {
            query = query.Where(x => x.DestinationId == filter.DestinationId.Value);
        }

        if (filter.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == filter.IsEnabled.Value);
        }

        var totalCount = await query.CountAsync(ct);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var desc = string.Equals(sortOrder, "desc", StringComparison.OrdinalIgnoreCase);
        query = sortBy?.ToLowerInvariant() switch
        {
            "resourcetype"       => desc ? query.OrderByDescending(x => x.ResourceType)                  : query.OrderBy(x => x.ResourceType),
            "destinationobject"  => desc ? query.OrderByDescending(x => x.DestinationObject)              : query.OrderBy(x => x.DestinationObject),
            "isenabled"          => desc ? query.OrderByDescending(x => x.IsEnabled)                      : query.OrderBy(x => x.IsEnabled),
            "createdonutc"       => desc ? query.OrderByDescending(x => x.CreatedOnUtc)                   : query.OrderBy(x => x.CreatedOnUtc),
            "modifiedonutc"      => desc ? query.OrderByDescending(x => x.ModifiedOnUtc ?? x.CreatedOnUtc): query.OrderBy(x => x.ModifiedOnUtc ?? x.CreatedOnUtc),
            _                    => desc ? query.OrderByDescending(x => x.Name)                           : query.OrderBy(x => x.Name),
        };

        var items = await query
            .Skip(skip)
            .Take(take)
            .ToListAsync(ct);

        return new PagedResult<MappingProfile>(items, totalCount, page, take);
    }

    public async Task<MappingProfile?> GetMappingProfileAsync(Guid id, CancellationToken ct) =>
        await _db.MappingProfiles.Include(x => x.Fields).FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<MappingProfile?> FindMappingProfileAsync(
        string resourceType, Guid sourceConnectionId, Guid destinationId, CancellationToken ct) =>
        await _db.MappingProfiles
            .Include(x => x.Fields)
            .FirstOrDefaultAsync(
                x => x.ResourceType == resourceType
                    && x.SourceConnectionId == sourceConnectionId
                    && x.DestinationId == destinationId,
                ct);

    public async Task AddMappingProfileAsync(MappingProfile e, CancellationToken ct)
    {
        await _db.MappingProfiles.AddAsync(e, ct);
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpdateMappingProfileAsync(MappingProfile e, CancellationToken ct)
    {
        await _db.SaveChangesAsync(ct);

        // MappingProfile.Update() replaces the entire Fields collection in memory (see MappingProfile.ReplaceFields:
        // Clear + AddRange). A definitive cleanup pass below removes anything left over for this profile that isn't
        // one of the ids SaveChangesAsync just persisted, guaranteeing "update" is a real full replace — a field
        // dropped from the mapping is deleted, not left behind as an orphan that keeps feeding future pipeline runs
        // regardless of what the UI currently shows. ExecuteDeleteAsync runs directly against the database (no
        // change-tracker involvement), so it can't conflict with the save that just happened.
        var currentFieldIds = e.Fields
            .Select(f => _db.Entry(f).Property<Guid>("Id").CurrentValue)
            .ToList();

        // MappingField is owned (OwnsMany), so it has no queryable DbSet of its own — EF requires navigating to it
        // through its owner.
        await _db.MappingProfiles
            .Where(p => p.Id == e.Id)
            .SelectMany(p => p.Fields)
            .Where(f => !currentFieldIds.Contains(EF.Property<Guid>(f, "Id")))
            .ExecuteDeleteAsync(ct);

        // Same owned-reference-replacement corruption as UpdateSourceConnectionAsync above — clear the tracker so a
        // later Get*Async for this id within the same request's DbContext re-materializes cleanly instead of
        // hitting the corrupted tracked entry/orphans.
        _db.ChangeTracker.Clear();
    }

    public Task RemoveMappingProfileAsync(MappingProfile e, CancellationToken ct)
    {
        // MappingProfile is ISoftDeletable: AuditingSaveChangesInterceptor converts this Remove into a soft delete
        // (IsDeleted/DeletedBy/DeletedOnUtc) rather than issuing a physical DELETE.
        _db.MappingProfiles.Remove(e);
        return _db.SaveChangesAsync(ct);
    }

    // ── Resource pipeline routes ──────────────────────────────────────────────
    public async Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesAsync(CancellationToken ct) =>
        await _db.ResourcePipelineRoutes
            .Include(x => x.ResourceMappings)
            .AsSplitQuery()
            .OrderBy(x => x.Priority)
            .ThenBy(x => x.Id)
            .ToListAsync(ct);

    public async Task<ResourcePipelineRoute?> GetRouteAsync(Guid id, CancellationToken ct) =>
        await _db.ResourcePipelineRoutes
            .Include(x => x.ResourceMappings)
            .AsSplitQuery()
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
