using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Backs the "SSO Configurations" admin screen: reads/writes the SAML and magic-link fields as live,
/// DB-backed <c>SystemSetting</c> rows (via <see cref="Abstractions.Caching.ISystemSettingsCache"/>),
/// so a save here takes effect immediately — no appsettings edit or service restart needed.
/// </summary>
public interface ISsoConfigurationsService
{
    Task<SsoConfigurationsDto> GetAsync(CancellationToken cancellationToken);

    Task<SsoConfigurationsDto> UpdateAsync(UpdateSsoConfigurationsRequest request, CancellationToken cancellationToken);
}
