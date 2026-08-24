using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Tenancy;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Looks up a user's TenantId fresh from the database, cached for a short window per user — same pattern
/// and same cache duration as <see cref="CachedUserPermissionsProvider"/>, for the same reasons (bounded
/// staleness window instead of an unbounded one from a JWT claim). Registered as a singleton, resolves
/// <see cref="IUserAccessRepository"/> lazily through a scope so it works with either the EF or in-memory
/// repository registration.
/// </summary>
public sealed class CachedCurrentTenantResolver : ICurrentTenantResolver
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    public CachedCurrentTenantResolver(IServiceScopeFactory scopeFactory, IMemoryCache cache)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
    }

    public async Task<Guid?> ResolveTenantIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cacheKey = $"user-tenant:{userId}";
        if (_cache.TryGetValue(cacheKey, out Guid cached))
        {
            return cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUserAccessRepository>();

        var user = await repository.GetUserByIdAsync(userId, cancellationToken);
        if (user is null)
        {
            return null;
        }

        _cache.Set(cacheKey, user.TenantId, CacheDuration);
        return user.TenantId;
    }
}
