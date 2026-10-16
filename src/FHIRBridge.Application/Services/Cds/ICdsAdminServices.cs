using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>Admin CRUD over CDS service instances (api/v1/cds/services).</summary>
public interface ICdsServiceInstanceService
{
    Task<IReadOnlyList<CdsServiceInstanceDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<CdsServiceInstanceDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CdsServiceInstanceDto> CreateAsync(SaveCdsServiceInstanceRequest request, CancellationToken cancellationToken);

    Task<CdsServiceInstanceDto> UpdateAsync(Guid id, SaveCdsServiceInstanceRequest request, CancellationToken cancellationToken);

    Task DeleteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The handler's synthetic sample request for the instance's hook (Test screen starting point).</summary>
    Task<CdsRequest> GetSampleRequestAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Admin CRUD over trusted issuers (api/v1/cds/trusted-issuers).</summary>
public interface ICdsTrustedIssuerService
{
    Task<IReadOnlyList<CdsTrustedIssuerDto>> GetAllAsync(CancellationToken cancellationToken);

    Task<CdsTrustedIssuerDto?> GetByIdAsync(Guid id, CancellationToken cancellationToken);

    Task<CdsTrustedIssuerDto> CreateAsync(SaveCdsTrustedIssuerRequest request, CancellationToken cancellationToken);

    Task<CdsTrustedIssuerDto> UpdateAsync(Guid id, SaveCdsTrustedIssuerRequest request, CancellationToken cancellationToken);

    /// <summary>Refused while any service instance lists the issuer in its allow-list.</summary>
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>The Epic setup sheet for one service (api/v1/cds/services/{id}/setup-sheet).</summary>
public interface ICdsSetupSheetService
{
    /// <param name="requestOrigin">"{scheme}://{host}" of the admin request, the fallback when Cds:PublicBaseUrl is unset.</param>
    /// <exception cref="FHIRBridge.SharedKernel.Exceptions.NotFoundException">No instance with that id.</exception>
    Task<CdsSetupSheetDto> GetAsync(Guid instanceId, string requestOrigin, CancellationToken cancellationToken);
}

/// <summary>
/// Checks that a trusted issuer's FHIR server is reachable for callbacks (api/v1/cds/trusted-issuers/{id}/test-callback):
/// GET {EffectiveFhirBaseUrl}/metadata only, no token, no patient data. Audited, never throws for a remote failure.
/// </summary>
public interface ICdsCallbackTestService
{
    /// <exception cref="FHIRBridge.SharedKernel.Exceptions.NotFoundException">No issuer with that id.</exception>
    Task<CdsCallbackTestResultDto> TestAsync(Guid trustedIssuerId, CancellationToken cancellationToken);
}

/// <summary>Read-only CDS admin views: handler catalog, discovery URLs, call activity.</summary>
public interface ICdsCatalogService
{
    IReadOnlyList<CdsHandlerDto> GetHandlers();

    /// <param name="requestOrigin">"{scheme}://{host}" of the admin request, the fallback when Cds:PublicBaseUrl is unset.</param>
    Task<CdsDiscoveryInfoDto> GetDiscoveryInfoAsync(string requestOrigin, CancellationToken cancellationToken);

    Task<CdsActivityPageDto> GetActivityAsync(
        CdsCallLogFilter filter, int page, int pageSize, CancellationToken cancellationToken);
}
