using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>
/// Flat persistence for the configuration entities that used to live under the (now removed) Tenant aggregate.
/// Each entity type is stored and fetched independently; joins between them are done in memory by the consuming
/// services (mapping → source/destination, route → mapping, etc.).
/// </summary>
public interface IConfigurationRepository
{
    // ── Source connections ────────────────────────────────────────────────────
    Task<IReadOnlyList<SourceConnection>> GetSourceConnectionsAsync(CancellationToken ct);
    Task<SourceConnection?> GetSourceConnectionAsync(Guid id, CancellationToken ct);
    Task AddSourceConnectionAsync(SourceConnection e, CancellationToken ct);
    Task UpdateSourceConnectionAsync(SourceConnection e, CancellationToken ct);
    Task DeleteSourceConnectionAsync(SourceConnection sourceConnection, CancellationToken cancellationToken);

    /// <summary>Case-insensitive name uniqueness check across non-soft-deleted source connections. When
    /// <paramref name="excludeId"/> is set (an update in progress), that connection's own row is excluded so it
    /// doesn't collide with itself.</summary>
    Task<bool> ExistsWithNameAsync(string name, Guid? excludeId, CancellationToken cancellationToken);

    // ── Destinations ──────────────────────────────────────────────────────────
    Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct);
    Task<DestinationConfiguration?> GetDestinationAsync(Guid id, CancellationToken ct);
    Task AddDestinationAsync(DestinationConfiguration e, CancellationToken ct);
    Task UpdateDestinationAsync(DestinationConfiguration e, CancellationToken ct);

    // ── Mapping profiles ──────────────────────────────────────────────────────
    Task<IReadOnlyList<MappingProfile>> GetMappingProfilesAsync(CancellationToken ct);
    Task<MappingProfile?> GetMappingProfileAsync(Guid id, CancellationToken ct);
    Task AddMappingProfileAsync(MappingProfile e, CancellationToken ct);
    Task UpdateMappingProfileAsync(MappingProfile e, CancellationToken ct);

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
