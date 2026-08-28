using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Healow interactive SMART client. Healow authenticates as a public OAuth 2.0 client using the
/// authorization-code + PKCE grant; all of that behavior now lives in <see cref="SmartAuthorizationCodeTokenProvider"/>.
/// This subclass only pins the provider name so log/audit lines and the token-store key stay Healow-specific.
/// </summary>
public sealed class HealowAuthorizationCodeTokenProvider : SmartAuthorizationCodeTokenProvider
{
    public HealowAuthorizationCodeTokenProvider(
        HttpClient httpClient,
        IFhirAuthorizationCodeTokenStore tokenStore,
        IBackendServicesJwtFactory? jwtFactory = null,
        ILogger<SmartAuthorizationCodeTokenProvider>? logger = null)
        : base(httpClient, tokenStore, jwtFactory, logger)
    {
    }

    protected override string ProviderName => "Healow";
}
