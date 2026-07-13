using FHIRBridge.Application.Abstractions.Persistence;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// A queryable record of lineage events so the full chain of custody for a resource (access → normalize → de-id →
/// output) can be reconstructed. The <see cref="ILineageTracker"/> appends here in addition to the audit log.
/// </summary>
public interface ILineageStore
{
    Task AppendAsync(ResourceLineageRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<ResourceLineageRecord>> QueryAsync(LineageQuery query, CancellationToken cancellationToken);
}

public sealed record LineageQuery(
    Guid? PipelineRunId = null,
    string? ResourceType = null,
    string? SourceResourceId = null);

/// <summary>The ordered chain of lineage events reconstructed for a resource.</summary>
public sealed record ResourceLineageChain(
    string? SourceResourceId,
    IReadOnlyList<ResourceLineageRecord> Steps);

/// <summary>Reconstructs the end-to-end lineage chain for a resource from the lineage store.</summary>
public interface ILineageQueryService
{
    Task<ResourceLineageChain> GetChainAsync(LineageQuery query, CancellationToken cancellationToken);

    Task<PagedResult<ResourceLineageRecord>> GetPagedAsync(
        LineageListFilter filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}

/// <summary>Filters for browsing lineage entries as a flat, paginated list (as opposed to one resource's chain).</summary>
public sealed record LineageListFilter(
    Guid? PipelineRunId,
    string? ResourceType,
    string? Action,
    string? Status);
