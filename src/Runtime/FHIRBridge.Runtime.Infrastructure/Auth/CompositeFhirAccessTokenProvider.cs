using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Selects the access-token grant per source across the two Bridge axes.
/// <para>
/// The <b>application-type</b> axis (composition) takes precedence when set: the source's
/// <see cref="ApplicationType"/> is resolved to an <see cref="ISourceApplicationStrategy"/> from the registry, which
/// owns the grant/launch flow for that type. Dispatch is a registry lookup rather than a switch, so a new
/// application type is a new strategy plus one registration (enforced by the no-switch architecture test).
/// </para>
/// <para>
/// When no application type is set the legacy per-vendor inference applies: Healow pins the interactive PKCE flow and
/// MEDITECH Greenfield a confidential-client JSON exchange; otherwise a private key implies SMART Backend Services
/// (RS384 JWT) and a client secret implies OAuth 2.0 client-credentials. Sources with neither are unauthenticated.
/// </para>
/// </summary>
public sealed class CompositeFhirAccessTokenProvider : IFhirAccessTokenProvider
{
    private readonly ISourceApplicationStrategyRegistry _applicationStrategies;
    private readonly EpicAccessTokenProvider _smartBackendServices;
    private readonly OAuth2ClientCredentialsTokenProvider _clientCredentials;
    private readonly HealowAuthorizationCodeTokenProvider _healow;
    private readonly MeditechGreenfieldTokenProvider _meditechGreenfield;

    public CompositeFhirAccessTokenProvider(
        ISourceApplicationStrategyRegistry applicationStrategies,
        EpicAccessTokenProvider smartBackendServices,
        OAuth2ClientCredentialsTokenProvider clientCredentials,
        HealowAuthorizationCodeTokenProvider healow,
        MeditechGreenfieldTokenProvider meditechGreenfield)
    {
        _applicationStrategies = applicationStrategies;
        _smartBackendServices = smartBackendServices;
        _clientCredentials = clientCredentials;
        _healow = healow;
        _meditechGreenfield = meditechGreenfield;
    }

    public Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        // Application-type axis (composition): resolve the strategy from the registry — no switch on ApplicationType.
        if (source.ApplicationType is { } applicationType)
        {
            return _applicationStrategies.Resolve(applicationType).GetAccessTokenAsync(source, cancellationToken);
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
