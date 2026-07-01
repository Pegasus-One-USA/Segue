using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Epic interactive SMART client for the EHR-launch and provider/patient standalone application types (the
/// interactive counterpart to the non-interactive <see cref="EpicAccessTokenProvider"/> Backend Services grant).
/// Epic completes the authorization-code + PKCE grant like any other SMART app, so the behavior lives entirely in
/// <see cref="SmartAuthorizationCodeTokenProvider"/>; this subclass only pins the provider name.
/// </summary>
public sealed class EpicInteractiveTokenProvider : SmartAuthorizationCodeTokenProvider
{
    public EpicInteractiveTokenProvider(
        HttpClient httpClient,
        IFhirAuthorizationCodeTokenStore tokenStore,
        IFhirAccessTokenAuditSink? auditSink = null)
        : base(httpClient, tokenStore, auditSink)
    {
    }

    protected override string ProviderName => "Epic";
}
