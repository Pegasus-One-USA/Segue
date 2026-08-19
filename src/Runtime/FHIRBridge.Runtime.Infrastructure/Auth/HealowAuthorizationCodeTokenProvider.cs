using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Healow (eClinicalWorks) interactive SMART client. eCW authenticates as a public OAuth 2.0 client using the
/// authorization-code + PKCE grant, same as every other vendor's <see cref="SmartAuthorizationCodeTokenProvider"/>
/// flow — this subclass pins the provider name for Healow-specific log/audit lines and the token-store key, and
/// adds the one confirmed eCW-specific deviation: every /authorize request must also carry a <c>practice_code</c>
/// query parameter identifying which eCW practice's patient portal to authenticate against (confirmed against a
/// live eCW authorize request — see the ECW_Net proof-of-concept's EcwOptions.PracticeCode). eCW deploys its FHIR
/// API per-practice as <c>/fhir/r4/{practiceCode}</c>, so the code is simply the FHIR base URL's last path
/// segment — no separate config field is needed.
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

    protected override IReadOnlyDictionary<string, string> AdditionalAuthorizationParameters(FhirSourceConfiguration source)
    {
        var practiceCode = ExtractPracticeCode(source.BaseUrl);
        return string.IsNullOrWhiteSpace(practiceCode)
            ? base.AdditionalAuthorizationParameters(source)
            : new Dictionary<string, string> { ["practice_code"] = practiceCode };
    }

    /// <summary>
    /// eCW's FHIR base URL is always shaped <c>https://fhir4.healow.com/fhir/r4/{practiceCode}</c> — the last
    /// path segment IS the practice code the authorize request separately requires. Null when the base URL isn't
    /// configured/parseable yet (e.g. a source still being set up), matching AdditionalAuthorizationParameters'
    /// "nothing to add" default instead of sending a blank practice_code.
    /// </summary>
    private static string? ExtractPracticeCode(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[^1] : null;
    }
}
