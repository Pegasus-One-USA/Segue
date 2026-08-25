using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Branding;

/// <summary>
/// Reads/writes the global white-label branding configuration — the DB-backed replacement for the portal's
/// former mock-JSON-fixture + localStorage "persistence" (see BrandingService.ts remarks). Admin-editable
/// from the Settings hub's Branding tab; the GET side is also called anonymously (login page, before a
/// session exists) since none of this data is sensitive.
/// </summary>
public interface IBrandConfigurationService
{
    /// <summary>tenantId must already be resolved server-side by the caller — never accepted from
    /// client-supplied request data.</summary>
    Task<BrandConfigurationDto> GetAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<BrandConfigurationDto> UpdateAsync(
        Guid tenantId, UpdateBrandConfigurationRequest request, CancellationToken cancellationToken);
}
