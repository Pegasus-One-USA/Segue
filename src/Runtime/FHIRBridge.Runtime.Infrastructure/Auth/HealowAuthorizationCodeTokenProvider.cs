using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Healow (eClinicalWorks) interactive SMART client. This subclass pins the provider name for Healow-specific
/// log/audit lines and the token-store key. IMPORTANT: every ApplicationType strategy (Patient/Standalone/
/// EhrLaunch) delegates to the vendor-neutral <see cref="SmartAuthorizationCodeTokenProvider"/> base class
/// directly, regardless of vendor — this subclass is never actually instantiated for those flows, so an override
/// here has no effect on them. eCW's confirmed <c>practice_code</c> requirement is therefore handled as a plain
/// vendor check directly in the base class's <c>BuildAuthorizationRequest</c> instead of an override here (see its
/// own remarks). <see cref="IncludePkce"/> below is NOT currently effective for the same reason — kept as
/// documentation of a known eCW deviation pending the same base-class treatment.
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

    // Confirmed against a real, working eCW authorize request: it carries no code_challenge/code_challenge_method
    // at all (unlike every other vendor here). See SmartAuthorizationCodeTokenProvider.IncludePkce's own remarks
    // for why the token exchange itself needs no corresponding change.
    protected override bool IncludePkce => false;

    /// <summary>
    /// eCW's authorize endpoint rejects a SMART v2 granular scope suffix (<c>patient/Patient.rs</c>) with
    /// <c>invalid_scope</c> — confirmed against a live authorize attempt. Rewrites every resource scope's suffix
    /// to v1's coarse <c>.read</c> regardless of the SourceConnection's own configured/detected scope version, so
    /// an existing eCW connection saved with v2 scopes doesn't need to be re-saved by an admin to work. Non-resource
    /// scopes (<c>openid</c>, <c>fhirUser</c>, <c>offline_access</c>, <c>launch/patient</c>, ...) have no dot suffix
    /// and pass through unchanged.
    /// </summary>
    protected override string NormalizeScope(string resolvedScope)
    {
        var scopes = resolvedScope.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < scopes.Length; i++)
        {
            var scope = scopes[i];
            var slashIndex = scope.IndexOf('/');
            if (slashIndex < 0)
            {
                continue;
            }

            var afterSlash = scope[(slashIndex + 1)..];
            var dotIndex = afterSlash.LastIndexOf('.');
            if (dotIndex < 0)
            {
                continue;
            }

            scopes[i] = $"{scope[..(slashIndex + 1 + dotIndex)]}.read";
        }

        return string.Join(' ', scopes);
    }
}
