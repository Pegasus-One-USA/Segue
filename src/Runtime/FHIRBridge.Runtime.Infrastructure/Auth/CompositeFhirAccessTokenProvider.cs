using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Selects the access-token grant per source. Two source types pin a specific grant regardless of which credentials
/// are present: Healow uses an interactive authorization-code + PKCE flow, MEDITECH Greenfield a confidential-client
/// JSON token exchange. Otherwise the grant is inferred from the credentials — a private key (SMART Backend Services
/// RS384 JWT — Epic) takes precedence; a client secret uses the OAuth 2.0 client-credentials grant (Cerner /
/// Allscripts / generic FHIR). Sources with neither are treated as unauthenticated (empty token).
/// </summary>
public sealed class CompositeFhirAccessTokenProvider : IFhirAccessTokenProvider
{
    private readonly EpicAccessTokenProvider _smartBackendServices;
    private readonly OAuth2ClientCredentialsTokenProvider _clientCredentials;
    private readonly HealowAuthorizationCodeTokenProvider _healow;
    private readonly MeditechGreenfieldTokenProvider _meditechGreenfield;

    public CompositeFhirAccessTokenProvider(
        EpicAccessTokenProvider smartBackendServices,
        OAuth2ClientCredentialsTokenProvider clientCredentials,
        HealowAuthorizationCodeTokenProvider healow,
        MeditechGreenfieldTokenProvider meditechGreenfield)
    {
        _smartBackendServices = smartBackendServices;
        _clientCredentials = clientCredentials;
        _healow = healow;
        _meditechGreenfield = meditechGreenfield;
    }

    public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
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
