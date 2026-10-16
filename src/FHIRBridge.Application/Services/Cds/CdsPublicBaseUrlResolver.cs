using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Cds;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Application.Services.Cds;

/// <summary>
/// Same approach as <see cref="OAuthPublicOriginResolver"/>: a configured value (System Settings override, then
/// appsettings/env) is the URL actually registered with the EHR, so the proxy chain cannot change it. The
/// request's own origin is only a display fallback (IsConfigured false): CdsHooksService never derives the
/// expected JWT aud from it.
/// </summary>
public sealed class CdsPublicBaseUrlResolver : ICdsPublicBaseUrlResolver
{
    private readonly ISystemSettingsCache _settingsCache;
    private readonly IConfiguration _configuration;

    public CdsPublicBaseUrlResolver(ISystemSettingsCache settingsCache, IConfiguration configuration)
    {
        _settingsCache = settingsCache;
        _configuration = configuration;
    }

    public async Task<CdsPublicBaseUrl> ResolveAsync(string requestOrigin, CancellationToken cancellationToken)
    {
        var configured = await _settingsCache.GetStringAsync(
            CdsPublicBaseUrl.ConfigurationKey,
            _configuration[CdsPublicBaseUrl.ConfigurationKey] ?? string.Empty,
            cancellationToken);

        return string.IsNullOrWhiteSpace(configured)
            ? new CdsPublicBaseUrl(requestOrigin.TrimEnd('/'), IsConfigured: false)
            : new CdsPublicBaseUrl(configured.Trim().TrimEnd('/'), IsConfigured: true);
    }
}
