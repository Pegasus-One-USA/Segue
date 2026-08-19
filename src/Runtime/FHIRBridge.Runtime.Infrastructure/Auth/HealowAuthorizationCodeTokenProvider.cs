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
