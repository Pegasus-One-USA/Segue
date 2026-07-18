using FHIRBridge.Governance;
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
public sealed class CompositeFhirAccessTokenProvider : IFhirAccessTokenProvider, IFhirPatientContextProvider
{
    private readonly ISourceApplicationStrategyRegistry _applicationStrategies;
    private readonly EpicAccessTokenProvider _smartBackendServices;
    private readonly OAuth2ClientCredentialsTokenProvider _clientCredentials;
    private readonly HealowAuthorizationCodeTokenProvider _healow;
    private readonly MeditechGreenfieldTokenProvider _meditechGreenfield;
    private readonly IGovernanceLogger _governanceLogger;

    public CompositeFhirAccessTokenProvider(
        ISourceApplicationStrategyRegistry applicationStrategies,
        EpicAccessTokenProvider smartBackendServices,
        OAuth2ClientCredentialsTokenProvider clientCredentials,
        HealowAuthorizationCodeTokenProvider healow,
        MeditechGreenfieldTokenProvider meditechGreenfield,
        IGovernanceLogger governanceLogger)
    {
        _applicationStrategies = applicationStrategies;
        _smartBackendServices = smartBackendServices;
        _clientCredentials = clientCredentials;
        _healow = healow;
        _meditechGreenfield = meditechGreenfield;
        _governanceLogger = governanceLogger;
    }

    public async Task<string> GetAccessTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        // This is the single dispatch point every vendor/grant-type token acquisition (fresh mint or refresh
        // on a cache miss — see DistributedFhirAccessTokenCache, which wraps this provider) funnels through,
        // so logging here once covers all of them instead of touching each per-vendor provider.
        var grantType = DetermineGrantType(source);

        try
        {
            var token = await ResolveTokenAsync(source, grantType, cancellationToken);

            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry($"OAuth:{grantType}", Success: !string.IsNullOrEmpty(token), source.Name),
                cancellationToken);

            return token;
        }
        catch (Exception exception)
        {
            await _governanceLogger.LogAuthenticationAsync(
                new AuthenticationEntry($"OAuth:{grantType}", Success: false, source.Name, exception.Message),
                cancellationToken);
            throw;
        }
    }

    private Task<string> ResolveTokenAsync(FhirSourceConfiguration source, string grantType, CancellationToken cancellationToken)
    {
        // Application-type axis (composition): resolve the strategy from the registry — no switch on ApplicationType.
        if (source.ApplicationType is { } applicationType)
        {
            return _applicationStrategies.Resolve(applicationType).GetAccessTokenAsync(source, cancellationToken);
        }

        // Legacy inference (no application type set): vendor-pinned grants first, then credential-based.
        return grantType switch
        {
            "Healow" => _healow.GetAccessTokenAsync(source, cancellationToken),
            "MeditechGreenfield" => _meditechGreenfield.GetAccessTokenAsync(source, cancellationToken),
            "BackendServices" => _smartBackendServices.GetAccessTokenAsync(source, cancellationToken),
            "ClientCredentials" => _clientCredentials.GetAccessTokenAsync(source, cancellationToken),
            _ => Task.FromResult(string.Empty)
        };
    }

    // Labels the grant type for the governance log — mirrors ResolveTokenAsync's own dispatch order exactly,
    // computed once up front so both the success and failure log entries (and ResolveTokenAsync's own legacy
    // branch) agree on the same label.
    private static string DetermineGrantType(FhirSourceConfiguration source)
    {
        if (source.ApplicationType is { } applicationType)
        {
            return $"ApplicationType:{applicationType}";
        }

        if (source.SourceType == RuntimeSourceType.Healow)
        {
            return "Healow";
        }

        if (source.SourceType == RuntimeSourceType.MeditechGreenfield)
        {
            return "MeditechGreenfield";
        }

        if (!string.IsNullOrWhiteSpace(source.PrivateKeyPem))
        {
            return "BackendServices";
        }

        if (!string.IsNullOrWhiteSpace(source.ClientSecret))
        {
            return "ClientCredentials";
        }

        return "None";
    }

    /// <summary>
    /// Resolves the launched patient context via the same registry dispatch as token acquisition: interactive
    /// application-type strategies expose the launch <c>patient</c> id; all other grants return null.
    /// </summary>
    public Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (source.ApplicationType is { } applicationType)
        {
            return _applicationStrategies.Resolve(applicationType).GetPatientContextAsync(source, cancellationToken);
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Resolves the launch-established FHIR base URL via the same registry dispatch as token acquisition:
    /// interactive application-type strategies expose it; all other grants return null.
    /// </summary>
    public Task<string?> GetResolvedBaseUrlAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (source.ApplicationType is { } applicationType)
        {
            return _applicationStrategies.Resolve(applicationType).GetResolvedBaseUrlAsync(source, cancellationToken);
        }

        return Task.FromResult<string?>(null);
    }

    /// <summary>
    /// Discards any cached token via the same registry dispatch as token acquisition: interactive application-type
    /// strategies clear their token store entry; all other grants (which mint tokens on demand rather than caching
    /// an interactive session) have nothing to discard.
    /// </summary>
    public Task DiscardTokenAsync(FhirSourceConfiguration source, CancellationToken cancellationToken)
    {
        if (source.ApplicationType is { } applicationType)
        {
            return _applicationStrategies.Resolve(applicationType).DiscardTokenAsync(source, cancellationToken);
        }

        return Task.CompletedTask;
    }
}
