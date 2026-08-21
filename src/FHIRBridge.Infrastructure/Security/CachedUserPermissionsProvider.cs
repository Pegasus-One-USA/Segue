using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Looks up a user's effective permission codes — the union of every role they hold, with any per-user
/// <see cref="Domain.Entities.PermissionAllocation"/> override applied on top — fresh from the database,
/// cached for a short window per user so this doesn't cost a DB round trip on every single request. This
/// is the exact same computation <c>LocalAuthService.GetPermissionCodesAsync</c> does at login time; the
/// two aren't unified into one shared method because login already has its roles loaded and needs the
/// result synchronously as part of building the login response, while this is looked up per-request by
/// only a user id.
///
/// Registered as a singleton (see <see cref="DependencyInjection"/>) and resolves
/// <see cref="IUserAccessRepository"/> lazily through a scope — same reasoning as
/// <see cref="Caching.InProcessSystemSettingsCache"/> — so it works whether the registered repository is
/// itself scoped (EF Core) or singleton (in-memory).
/// </summary>
public sealed class CachedUserPermissionsProvider : IUserPermissionsProvider
{
    // Short enough that an admin revoking a permission is reflected well within the same testing session;
    // long enough that a screen making several authorization-gated calls in quick succession doesn't hit
    // the database once per call.
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IMemoryCache _cache;

    public CachedUserPermissionsProvider(IServiceScopeFactory scopeFactory, IMemoryCache cache)
    {
        _scopeFactory = scopeFactory;
        _cache = cache;
    }

    public async Task<IReadOnlyList<string>> GetEffectivePermissionCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var cacheKey = $"user-permissions:{userId}";
        if (_cache.TryGetValue(cacheKey, out IReadOnlyList<string>? cached) && cached is not null)
        {
            return cached;
        }

        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IUserAccessRepository>();

        var roles = await repository.GetUserRolesAsync(userId, cancellationToken);
        var roleCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            var perms = await repository.GetRolePermissionsAsync(role.Id, cancellationToken);
            foreach (var p in perms)
            {
                roleCodes.Add(p.Name);
            }
        }

        var userAllocations = await repository.GetUserPermissionAllocationsAsync(userId, cancellationToken);
        var overridden = new HashSet<string>(
            userAllocations.Select(a => a.Permission.Name), StringComparer.OrdinalIgnoreCase);

        var result = new HashSet<string>(
            roleCodes.Where(code => !overridden.Contains(code)), StringComparer.OrdinalIgnoreCase);

        foreach (var allocation in userAllocations)
        {
            if (allocation.IsEnabled)
            {
                result.Add(allocation.Permission.Name);
            }
        }

        IReadOnlyList<string> codes = [.. result];
        _cache.Set(cacheKey, codes, CacheDuration);
        return codes;
    }
}
