using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// In-memory implementation of <see cref="IConfigurationRepository"/> for local/dev runs with no connection string.
/// One <see cref="ConcurrentDictionary{TKey,TValue}"/> per entity type. Add and Update both upsert by Id, mirroring
/// the previous in-memory tenant repository.
/// </summary>
public sealed class InMemoryConfigurationRepository : IConfigurationRepository
{
    private readonly ConcurrentDictionary<Guid, SourceConnection> _sources = new();
    private readonly ConcurrentDictionary<Guid, DestinationConfiguration> _destinations = new();
    private readonly ConcurrentDictionary<Guid, MappingProfile> _mappingProfiles = new();
    private readonly ConcurrentDictionary<Guid, ResourcePipelineRoute> _routes = new();
    private readonly ConcurrentDictionary<Guid, WebhookConfiguration> _webhooks = new();

    // ── Source connections ────────────────────────────────────────────────────
    public Task<IReadOnlyList<SourceConnection>> GetSourceConnectionsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceConnection>>(_sources.Values.OrderBy(x => x.Name).ToList());

    public Task<SourceConnection?> GetSourceConnectionAsync(Guid id, CancellationToken ct)
    {
        _sources.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task AddSourceConnectionAsync(SourceConnection e, CancellationToken ct)
    {
        _sources[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task UpdateSourceConnectionAsync(SourceConnection e, CancellationToken ct)
    {
        _sources[e.Id] = e;
        return Task.CompletedTask;
    }

    // ── Destinations ──────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DestinationConfiguration>>(_destinations.Values.OrderBy(x => x.Name).ToList());

    public Task<DestinationConfiguration?> GetDestinationAsync(Guid id, CancellationToken ct)
    {
        _destinations.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task AddDestinationAsync(DestinationConfiguration e, CancellationToken ct)
    {
        _destinations[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task UpdateDestinationAsync(DestinationConfiguration e, CancellationToken ct)
    {
        _destinations[e.Id] = e;
        return Task.CompletedTask;
    }

    // ── Mapping profiles ──────────────────────────────────────────────────────
    public Task<IReadOnlyList<MappingProfile>> GetMappingProfilesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<MappingProfile>>(_mappingProfiles.Values.OrderBy(x => x.Name).ToList());

    public Task<MappingProfile?> GetMappingProfileAsync(Guid id, CancellationToken ct)
    {
        _mappingProfiles.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task AddMappingProfileAsync(MappingProfile e, CancellationToken ct)
    {
        _mappingProfiles[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task UpdateMappingProfileAsync(MappingProfile e, CancellationToken ct)
    {
        _mappingProfiles[e.Id] = e;
        return Task.CompletedTask;
    }

    // ── Resource pipeline routes ──────────────────────────────────────────────
    public Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ResourcePipelineRoute>>(
            _routes.Values.OrderBy(x => x.Priority).ThenBy(x => x.Id).ToList());

    public Task<ResourcePipelineRoute?> GetRouteAsync(Guid id, CancellationToken ct)
    {
        _routes.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task AddRouteAsync(ResourcePipelineRoute e, CancellationToken ct)
    {
        _routes[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task UpdateRouteAsync(ResourcePipelineRoute e, CancellationToken ct)
    {
        _routes[e.Id] = e;
        return Task.CompletedTask;
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<WebhookConfiguration>> GetWebhooksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<WebhookConfiguration>>(_webhooks.Values.OrderBy(x => x.Name).ToList());

    public Task<WebhookConfiguration?> GetWebhookAsync(Guid id, CancellationToken ct)
    {
        _webhooks.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task AddWebhookAsync(WebhookConfiguration e, CancellationToken ct)
    {
        _webhooks[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task UpdateWebhookAsync(WebhookConfiguration e, CancellationToken ct)
    {
        _webhooks[e.Id] = e;
        return Task.CompletedTask;
    }

    // ── EHR endpoints ─────────────────────────────────────────────────────────
    // The EhrEndpoint directory is only ever populated by EpicEndpointDirectorySeeder against a real DbContext
    // (registered on the real-DB path only) — there is no in-memory equivalent seeding, so this always misses.
    // Callers already treat a missing EhrEndpoint as "fall back to the source connection's own base URL".
    public Task<EhrEndpoint?> GetEhrEndpointAsync(Guid id, CancellationToken ct) =>
        Task.FromResult<EhrEndpoint?>(null);
}
