using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Caching;

/// <summary>
/// Single-instance in-process cache: correct for the current one-VM deployment. If the API ever scales
/// to multiple instances, an admin edit on one instance won't invalidate the others' caches — at that
/// point this needs a pub/sub invalidation signal (e.g. Redis, already used for
/// DistributedFhirAccessTokenCache) instead of a plain in-memory field.
/// </summary>
public sealed class InProcessAllowedCorsOriginsCache : IAllowedCorsOriginsCache
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReadOnlySet<string> _configuredFloor;
    private volatile IReadOnlySet<string>? _cached;

    public InProcessAllowedCorsOriginsCache(IServiceScopeFactory scopeFactory, IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _configuredFloor = (configuration.GetSection("Portal:AllowedOrigins").Get<string[]>()
                ?? ["http://localhost:4200", "https://localhost:4200"])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlySet<string>> GetOriginsAsync(CancellationToken cancellationToken)
    {
        var snapshot = _cached;
        if (snapshot is not null)
        {
            return snapshot;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAllowedCorsOriginRepository>();
        var dbOrigins = await repository.GetAllAsync(cancellationToken);

        var merged = new HashSet<string>(_configuredFloor, StringComparer.OrdinalIgnoreCase);
        foreach (var origin in dbOrigins)
        {
            merged.Add(origin.OriginUrl);
        }

        _cached = merged;
        return merged;
    }

    public void Invalidate() => _cached = null;
}
