using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

public interface ITenantRepository
{
    Task<IReadOnlyList<Tenant>> GetAllAsync(CancellationToken cancellationToken);

    Task<Tenant?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Case-insensitive lookup by the URL-safe slug — the pre-login (<c>?tenant=code</c>) and
    /// migration (well-known "default" code) resolution paths both go through this.</summary>
    Task<Tenant?> GetByCodeAsync(string code, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(string code, Guid? excludingId, CancellationToken cancellationToken);

    /// <summary>Whether any user currently belongs to this tenant — used to block deleting a tenant out
    /// from under its users (the FK is RESTRICT, but the service checks first for a clean error message).</summary>
    Task<bool> HasUsersAsync(Guid tenantId, CancellationToken cancellationToken);

    Task AddAsync(Tenant tenant, CancellationToken cancellationToken);

    Task UpdateAsync(Tenant tenant, CancellationToken cancellationToken);

    Task DeleteAsync(Tenant tenant, CancellationToken cancellationToken);
}
