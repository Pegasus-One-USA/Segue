using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Flat persistence for the configuration entities that used to live under the (now removed) Tenant aggregate.
/// Each entity type is stored and fetched independently; joins between them are done in memory by the consuming
/// services (mapping → source/destination, route → mapping, etc.).
/// </summary>
public interface IConfigurationRepository
{
    /// <summary>
    /// Opens a transaction spanning multiple subsequent writes on this repository (see
    /// <see cref="IConfigurationTransaction"/>) — used by callers that create several related entities in
    /// sequence (e.g. a workflow's source, destination, and mapping profiles) and need all-or-nothing semantics
    /// rather than each write committing independently.
    /// </summary>
    Task<IConfigurationTransaction> BeginTransactionAsync(CancellationToken cancellationToken);

    // ── Source connections ────────────────────────────────────────────────────
    Task<IReadOnlyList<SourceConnection>> GetSourceConnectionsAsync(CancellationToken ct);
    Task<PagedResult<SourceConnection>> GetSourceConnectionsPagedAsync(
        SourceConnectionFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct);
    Task<SourceConnection?> GetSourceConnectionAsync(Guid id, CancellationToken ct);
    Task AddSourceConnectionAsync(SourceConnection e, CancellationToken ct);
    Task UpdateSourceConnectionAsync(SourceConnection e, CancellationToken ct);
    Task DeleteSourceConnectionAsync(SourceConnection sourceConnection, CancellationToken cancellationToken);

    /// <summary>Case-insensitive name uniqueness check across non-soft-deleted source connections. When
    /// <paramref name="excludeId"/> is set (an update in progress), that connection's own row is excluded so it
    /// doesn't collide with itself.</summary>
    Task<bool> ExistsWithNameAsync(string name, Guid? excludeId, CancellationToken cancellationToken);

    // ── Source configurations (workflow-specific retrieval/scopes for a reusable SourceConnection) ────────────
    Task<IReadOnlyList<SourceConfiguration>> GetSourceConfigurationsAsync(CancellationToken ct);
    Task<SourceConfiguration?> GetSourceConfigurationAsync(Guid id, CancellationToken ct);
    Task AddSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct);
    Task UpdateSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct);
    Task DeleteSourceConfigurationAsync(SourceConfiguration sourceConfiguration, CancellationToken cancellationToken);

    // ── Destinations ──────────────────────────────────────────────────────────
    Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct);
    Task<PagedResult<DestinationConfiguration>> GetDestinationsPagedAsync(
        DestinationFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct);
    Task<DestinationConfiguration?> GetDestinationAsync(Guid id, CancellationToken ct);
    Task AddDestinationAsync(DestinationConfiguration e, CancellationToken ct);
    Task UpdateDestinationAsync(DestinationConfiguration e, CancellationToken ct);
    Task RemoveDestinationAsync(DestinationConfiguration e, CancellationToken ct);

    /// <summary>
    /// True if any <see cref="PipelineRunRouteExecution"/> exists for a <see cref="MappingProfile"/> that targets
    /// this destination. There is no FK/navigation for this chain (destinations, mappings, and executions are all
    /// flat/independent), so it is resolved as two queries rather than a single join.
    /// </summary>
    Task<bool> HasDestinationExecutionHistoryAsync(Guid destinationId, CancellationToken ct);

    // ── Mapping profiles ──────────────────────────────────────────────────────
    Task<IReadOnlyList<MappingProfile>> GetMappingProfilesAsync(CancellationToken ct);
    Task<PagedResult<MappingProfile>> GetMappingProfilesPagedAsync(
        MappingProfileFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken ct);
    Task<MappingProfile?> GetMappingProfileAsync(Guid id, CancellationToken ct);

    /// <summary>Looks up an existing mapping profile for the same (ResourceType, SourceConnectionId,
    /// DestinationId) triple — the idempotency key the mapping-config import endpoint upserts on.</summary>
    Task<MappingProfile?> FindMappingProfileAsync(
        string resourceType, Guid sourceConnectionId, Guid destinationId, CancellationToken ct);

    /// <summary>Every profile targeting one destination. Used by the workflow-save cleanup pass to retire
    /// the profiles of a destination that has just been removed from its workflow, including any whose id
    /// was never stamped back onto a node and so can't be found from the node configuration alone.</summary>
    Task<IReadOnlyList<MappingProfile>> GetMappingProfilesByDestinationAsync(Guid destinationId, CancellationToken ct);

    Task AddMappingProfileAsync(MappingProfile e, CancellationToken ct);
    Task UpdateMappingProfileAsync(MappingProfile e, CancellationToken ct);
    Task RemoveMappingProfileAsync(MappingProfile e, CancellationToken ct);

    // ── Resource pipeline routes ──────────────────────────────────────────────
    Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesAsync(CancellationToken ct);
    Task<ResourcePipelineRoute?> GetRouteAsync(Guid id, CancellationToken ct);
    Task AddRouteAsync(ResourcePipelineRoute e, CancellationToken ct);
    Task UpdateRouteAsync(ResourcePipelineRoute e, CancellationToken ct);

    // ── Webhooks ──────────────────────────────────────────────────────────────
    Task<IReadOnlyList<WebhookConfiguration>> GetWebhooksAsync(CancellationToken ct);
    Task<WebhookConfiguration?> GetWebhookAsync(Guid id, CancellationToken ct);
    Task AddWebhookAsync(WebhookConfiguration e, CancellationToken ct);
    Task UpdateWebhookAsync(WebhookConfiguration e, CancellationToken ct);

    // ── EHR endpoints (read-only directory, populated by EpicEndpointDirectorySeeder) ──────────────
    Task<EhrEndpoint?> GetEhrEndpointAsync(Guid id, CancellationToken ct);
}

public sealed record DestinationFilter(
    string? Search,
    DestinationType? DestinationType,
    bool? IsEnabled);


public sealed record SourceConnectionFilter(
    string? Search,
    SourceSystemType? SourceSystemType,
    ApplicationType? ApplicationType,
    bool? IsEnabled);

public sealed record MappingProfileFilter(
    string? Search,
    string? ResourceType,
    Guid? SourceConnectionId,
    Guid? DestinationId,
    bool? IsEnabled);
