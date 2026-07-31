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
    private readonly ConcurrentDictionary<Guid, SourceConfiguration> _sourceConfigurations = new();
    private readonly ConcurrentDictionary<Guid, DestinationConfiguration> _destinations = new();
    private readonly ConcurrentDictionary<Guid, MappingProfile> _mappingProfiles = new();
    private readonly ConcurrentDictionary<Guid, ResourcePipelineRoute> _routes = new();
    private readonly ConcurrentDictionary<Guid, WebhookConfiguration> _webhooks = new();

    // No-op: this repository is only used for local/dev runs with no connection string (see class remarks) and
    // writes straight into the in-memory dictionaries with no staging to roll back. Real transactional
    // all-or-nothing semantics are only meaningful — and only provided — against the real EF-backed repository.
    public Task<IConfigurationTransaction> BeginTransactionAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IConfigurationTransaction>(new NoOpConfigurationTransaction());

    private sealed class NoOpConfigurationTransaction : IConfigurationTransaction
    {
        public Task CommitAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RollbackAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

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

    public Task DeleteSourceConnectionAsync(SourceConnection sourceConnection, CancellationToken cancellationToken)
    {
        _sources.TryRemove(sourceConnection.Id, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ExistsWithNameAsync(string name, Guid? excludeId, CancellationToken cancellationToken)
    {
        var exists = _sources.Values.Any(x =>
            string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (excludeId is null || x.Id != excludeId));
        return Task.FromResult(exists);
    }

    // ── Source configurations ─────────────────────────────────────────────────
    public Task<IReadOnlyList<SourceConfiguration>> GetSourceConfigurationsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<SourceConfiguration>>(_sourceConfigurations.Values.OrderBy(x => x.Name).ToList());

    public Task<SourceConfiguration?> GetSourceConfigurationAsync(Guid id, CancellationToken ct)
    {
        _sourceConfigurations.TryGetValue(id, out var e);
        return Task.FromResult(e);
    }

    public Task AddSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct)
    {
        _sourceConfigurations[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task UpdateSourceConfigurationAsync(SourceConfiguration e, CancellationToken ct)
    {
        _sourceConfigurations[e.Id] = e;
        return Task.CompletedTask;
    }

    public Task DeleteSourceConfigurationAsync(SourceConfiguration sourceConfiguration, CancellationToken cancellationToken)
    {
        _sourceConfigurations.TryRemove(sourceConfiguration.Id, out _);
        return Task.CompletedTask;
    }

    // ── Destinations ──────────────────────────────────────────────────────────
    public Task<IReadOnlyList<DestinationConfiguration>> GetDestinationsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DestinationConfiguration>>(_destinations.Values.OrderBy(x => x.Name).ToList());

    public Task<PagedResult<DestinationConfiguration>> GetDestinationsPagedAsync(
        DestinationFilter filter,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var query = _destinations.Values.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            query = query.Where(x => x.Name.Contains(filter.Search, StringComparison.OrdinalIgnoreCase));
        }

        if (filter.DestinationType.HasValue)
        {
            query = query.Where(x => x.DestinationType == filter.DestinationType.Value);
        }

        if (filter.IsEnabled.HasValue)
        {
            query = query.Where(x => x.IsEnabled == filter.IsEnabled.Value);
        }

        var descending = string.Equals(filter.SortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        var orderedQuery = string.Equals(filter.SortBy, "actionOn", StringComparison.OrdinalIgnoreCase)
            ? (descending
                ? query.OrderByDescending(x => x.ModifiedOnUtc ?? x.CreatedOnUtc)
                : query.OrderBy(x => x.ModifiedOnUtc ?? x.CreatedOnUtc))
            : query.OrderBy(x => x.Name);

        var ordered = orderedQuery.ToList();
        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);
        var items = ordered.Skip(skip).Take(take).ToList();

        return Task.FromResult(new PagedResult<DestinationConfiguration>(items, ordered.Count, page, take));
    }

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

    public Task RemoveDestinationAsync(DestinationConfiguration e, CancellationToken ct)
    {
        _destinations.TryRemove(e.Id, out _);
        return Task.CompletedTask;
    }

    // No in-memory store tracks pipeline run executions (they're only ever persisted against a real DbContext),
    // so there is never any recorded history to block a delete/edit against in this mode.
    public Task<bool> HasDestinationExecutionHistoryAsync(Guid destinationId, CancellationToken ct) =>
        Task.FromResult(false);

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
