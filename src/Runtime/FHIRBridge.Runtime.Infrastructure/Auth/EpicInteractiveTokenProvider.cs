using FHIRBridge.Runtime.Application.Abstractions.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Epic interactive SMART client for the EHR-launch and provider/patient standalone application types (the
/// interactive counterpart to the non-interactive <see cref="SmartBackendServicesTokenProvider"/> Backend Services grant).
/// Epic completes the authorization-code + PKCE grant like any other SMART app, so the behavior lives entirely in
/// <see cref="SmartAuthorizationCodeTokenProvider"/>; this subclass only pins the provider name.
/// </summary>
public sealed class EpicInteractiveTokenProvider : SmartAuthorizationCodeTokenProvider
{
    public EpicInteractiveTokenProvider(
        HttpClient httpClient,
        IFhirAuthorizationCodeTokenStore tokenStore,
        IBackendServicesJwtFactory? jwtFactory = null,
        ILogger<SmartAuthorizationCodeTokenProvider>? logger = null)
        : base(httpClient, tokenStore, jwtFactory, logger)
    {
    }

    protected override string ProviderName => "Epic";
}
