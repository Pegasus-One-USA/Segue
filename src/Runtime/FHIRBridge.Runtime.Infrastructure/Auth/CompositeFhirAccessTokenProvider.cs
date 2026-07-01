using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Selects the access-token grant per source across the two Bridge axes.
/// <para>
/// The <b>application-type</b> axis (composition) takes precedence when set: <see cref="ApplicationType.Backend"/>
/// mints a SMART Backend Services JWT, while the interactive types (EHR launch, provider/patient standalone) use the
/// vendor-neutral interactive authorization-code provider. This selection is independent of the vendor.
/// </para>
/// <para>
/// When no application type is set the legacy per-vendor inference applies: Healow pins the interactive PKCE flow and
/// MEDITECH Greenfield a confidential-client JSON exchange; otherwise a private key implies SMART Backend Services
/// (RS384 JWT) and a client secret implies OAuth 2.0 client-credentials. Sources with neither are unauthenticated.
/// </para>
/// </summary>
public sealed class CompositeFhirAccessTokenProvider : IFhirAccessTokenProvider
{
    private readonly EpicAccessTokenProvider _smartBackendServices;
    private readonly OAuth2ClientCredentialsTokenProvider _clientCredentials;
    private readonly SmartAuthorizationCodeTokenProvider _interactive;
    private readonly HealowAuthorizationCodeTokenProvider _healow;
    private readonly MeditechGreenfieldTokenProvider _meditechGreenfield;

    public CompositeFhirAccessTokenProvider(
        EpicAccessTokenProvider smartBackendServices,
        OAuth2ClientCredentialsTokenProvider clientCredentials,
        SmartAuthorizationCodeTokenProvider interactive,
        HealowAuthorizationCodeTokenProvider healow,
        MeditechGreenfieldTokenProvider meditechGreenfield)
    {
        _smartBackendServices = smartBackendServices;
        _clientCredentials = clientCredentials;
        _interactive = interactive;
        _healow = healow;
        _meditechGreenfield = meditechGreenfield;
    }

    public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        // Application-type axis (composition) — selects the SMART flow independently of the vendor.
        switch (source.ApplicationType)
        {
            case ApplicationType.Backend:
                return _smartBackendServices.GetAccessTokenAsync(source, cancellationToken);
            case ApplicationType.EhrLaunch:
            case ApplicationType.Standalone:
            case ApplicationType.Patient:
                return _interactive.GetAccessTokenAsync(source, cancellationToken);
        }

        // Legacy inference (no application type set): vendor-pinned grants first, then credential-based.
        switch (source.SourceType)
        {
            case RuntimeSourceType.Healow:
                return _healow.GetAccessTokenAsync(source, cancellationToken);
            case RuntimeSourceType.MeditechGreenfield:
                return _meditechGreenfield.GetAccessTokenAsync(source, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(source.PrivateKeyPem))
        {
            return _smartBackendServices.GetAccessTokenAsync(source, cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(source.ClientSecret))
        {
            return _clientCredentials.GetAccessTokenAsync(source, cancellationToken);
        }

        return Task.FromResult(string.Empty);
    }
}
