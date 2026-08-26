using System.Collections.Concurrent;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>Used when no database connection string is configured (dev only). Seeds the same well-known
/// Default Tenant the AddTenant migration creates in the real DB path, so both paths behave the same.</summary>
public sealed class InMemoryTenantRepository : ITenantRepository
{
    private readonly ConcurrentDictionary<Guid, Tenant> _tenants = new();

    public InMemoryTenantRepository()
    {
        var defaultTenant = new Tenant(
            SeededSecurityIds.DefaultTenantId, "Default Tenant", SeededSecurityIds.DefaultTenantCode);
        _tenants[defaultTenant.Id] = defaultTenant;
    }

    public Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Tenant>>(_tenants.Values.OrderBy(x => x.Name).ToList());

    public Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_tenants.GetValueOrDefault(id));

    public Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(_tenants.Values.FirstOrDefault(
            x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase)));

    public Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken) =>
        Task.FromResult(_tenants.Values.Any(
            x => string.Equals(x.Code, code, StringComparison.OrdinalIgnoreCase) && x.Id != excludingId));

    public Task<bool> HasUsersAsync(Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(false); // InMemoryUserAccessRepository has its own seed data, not checked here — dev-only path.

    public Task AddAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _tenants[tenant.Id] = tenant;
        return Task.CompletedTask;
    }

    public Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task DeleteAsync(Tenant tenant, CancellationToken cancellationToken)
    {
        _tenants.TryRemove(tenant.Id, out _);
        return Task.CompletedTask;
    }
}
