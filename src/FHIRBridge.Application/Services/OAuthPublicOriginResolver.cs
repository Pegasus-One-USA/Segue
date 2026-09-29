using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Application.Services;

/// <summary>
/// These URLs are registered verbatim with each EHR (eCW, Healow, Epic, ...) and MUST match exactly what the
/// browser actually ends up hitting — a scheme/host mismatch is a hard rejection at the EHR's authorize endpoint,
/// not a soft failure. Deriving them from the request's own Scheme/Host made that correctness depend on every
/// intermediary between the browser and the API process (WAF, CDN, Container Apps ingress, this app's own
/// Gateway) correctly forwarding/trusting X-Forwarded-Proto/Host — fragile by construction, since it's an
/// arbitrary number of hops whose behavior isn't under this app's control and can silently change. OAuth:
/// PublicBaseUrl, when set, sidesteps all of that: it's the one fixed, known-correct value that was registered
/// with the EHR in the first place, so nothing about the proxy chain in front of this process — present, absent,
/// or misconfigured — can affect it. The caller's request-derived origin remains the fallback for the genuinely
/// proxy-less case (direct-to-container access with no WAF/Front Door at all), where it's already accurate.
///
/// Read through <see cref="ISystemSettingsCache"/> (DB override, falling back to the OAuth:PublicBaseUrl
/// appsettings/env value) rather than raw IConfiguration alone, so an admin correcting this from Settings >
/// System Settings > OAuth takes effect on the very next request — no redeploy.
/// </summary>
public sealed class OAuthPublicOriginResolver : IOAuthPublicOriginResolver
{
    private readonly ISystemSettingsCache _settingsCache;
    private readonly IConfiguration _configuration;

    public OAuthPublicOriginResolver(ISystemSettingsCache settingsCache, IConfiguration configuration)
    {
        _settingsCache = settingsCache;
        _configuration = configuration;
    }

    public async Task<string> ResolveAsync(string requestDerivedOrigin, CancellationToken cancellationToken)
    {
        var configured = await _settingsCache.GetStringAsync(
            "OAuth:PublicBaseUrl", _configuration["OAuth:PublicBaseUrl"] ?? string.Empty, cancellationToken);
        return string.IsNullOrWhiteSpace(configured) ? requestDerivedOrigin : configured.TrimEnd('/');
    }
}
