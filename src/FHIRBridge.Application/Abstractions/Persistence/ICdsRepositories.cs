using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Access to <see cref="CdsTrustedIssuer"/> rows. Soft-deleted rows are never returned.</summary>
public interface ICdsTrustedIssuerRepository
{
    Task<IReadOnlyList<CdsTrustedIssuer>> GetAllAsync(CancellationToken cancellationToken);

    Task<CdsTrustedIssuer?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The enabled row whose Issuer equals <paramref name="issuer"/> exactly (ordinal), or null.</summary>
    Task<CdsTrustedIssuer?> GetEnabledByIssuerAsync(string issuer, CancellationToken cancellationToken);

    /// <summary>True when another non-deleted row already uses <paramref name="issuer"/>.</summary>
    Task<bool> IssuerExistsAsync(string issuer, Guid? excludeId, CancellationToken cancellationToken);

    Task AddAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken);

    Task UpdateAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken);

    Task DeleteAsync(CdsTrustedIssuer issuer, CancellationToken cancellationToken);
}

/// <summary>Access to <see cref="CdsServiceInstance"/> rows. Soft-deleted rows are never returned.</summary>
public interface ICdsServiceInstanceRepository
{
    Task<IReadOnlyList<CdsServiceInstance>> GetAllAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<CdsServiceInstance>> GetEnabledAsync(CancellationToken cancellationToken);

    Task<CdsServiceInstance?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>By URL slug (already normalized to lower case), enabled or not.</summary>
    Task<CdsServiceInstance?> GetByServiceIdAsync(string serviceId, CancellationToken cancellationToken);

    /// <summary>True when another non-deleted row already uses <paramref name="serviceId"/>.</summary>
    Task<bool> ServiceIdExistsAsync(string serviceId, Guid? excludeId, CancellationToken cancellationToken);

    Task AddAsync(CdsServiceInstance instance, CancellationToken cancellationToken);

    Task UpdateAsync(CdsServiceInstance instance, CancellationToken cancellationToken);

    Task DeleteAsync(CdsServiceInstance instance, CancellationToken cancellationToken);
}

/// <summary>Append-only CDS call log (the CDS Activity screen). Rows hold no PHI.</summary>
public interface ICdsCallLogRepository
{
    Task AddAsync(CdsCallLog entry, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<PagedResult<CdsCallLog>> GetPagedAsync(
        CdsCallLogFilter filter, int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>Filters for the CDS Activity listing; a null member is not applied.</summary>
/// <param name="ServiceId">Exact service id (URL slug).</param>
public sealed record CdsCallLogFilter(
    string? ServiceId = null,
    CdsCallKind? Kind = null,
    CdsCallOutcome? Outcome = null,
    DateTime? FromUtc = null,
    DateTime? ToUtc = null);
