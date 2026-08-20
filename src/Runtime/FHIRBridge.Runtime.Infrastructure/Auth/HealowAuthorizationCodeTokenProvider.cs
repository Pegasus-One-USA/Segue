using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Healow (eClinicalWorks) interactive SMART client. This subclass pins the provider name for Healow-specific
/// log/audit lines and the token-store key. IMPORTANT: every ApplicationType strategy (Patient/Standalone/
/// EhrLaunch) delegates to the vendor-neutral <see cref="SmartAuthorizationCodeTokenProvider"/> base class
/// directly, regardless of vendor — this subclass is never actually instantiated for those flows, so an override
/// here has no effect on them. eCW's confirmed deviations (no PKCE, no v2 granular scopes, mandatory
/// <c>practice_code</c>) are therefore all handled as plain <c>source.SourceType == RuntimeSourceType.Healow</c>
/// checks directly in the base class's <c>BuildAuthorizationRequest</c> instead of overrides here.
/// </summary>
public sealed class HealowAuthorizationCodeTokenProvider : SmartAuthorizationCodeTokenProvider
{
    public HealowAuthorizationCodeTokenProvider(
        HttpClient httpClient,
        IFhirAuthorizationCodeTokenStore tokenStore,
        IFhirAccessTokenAuditSink? auditSink = null,
        IBackendServicesJwtFactory? jwtFactory = null,
        ILogger<SmartAuthorizationCodeTokenProvider>? logger = null)
        : base(httpClient, tokenStore, auditSink, jwtFactory, logger)
    {
    }

    protected override string ProviderName => "Healow";
}
