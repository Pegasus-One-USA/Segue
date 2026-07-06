using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Orchestrates the interactive OAuth sign-in for a source connection. It resolves the source, discovers its SMART
/// authorization/token endpoints on demand, starts the authorization-code + PKCE flow, and completes it from the
/// callback — persisting the token so later pipeline runs read it back. For a route-scoped EHR launch it also runs
/// the launched pipeline route once the token is acquired. Source/route identifiers are carried as encrypted opaque
/// tokens (see <see cref="ILaunchTokenProtector"/>) so raw GUIDs never appear in a launch URL or the OAuth state.
/// </summary>
public sealed class InteractiveSourceAuthorizationService : IInteractiveSourceAuthorizationService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ISourceCapabilityDiscoveryService _discoveryService;
    private readonly IInteractiveAuthorizationFlow _authorizationFlow;
    private readonly IOAuthAuthorizationStateStore _stateStore;
    private readonly ISecretProvider _secretProvider;
    private readonly ILaunchTokenProtector _launchTokenProtector;
    private readonly IConfiguredPipelineService _pipelineService;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<InteractiveSourceAuthorizationService> _logger;

    public InteractiveSourceAuthorizationService(
        IConfigurationRepository configurationRepository,
        ISourceCapabilityDiscoveryService discoveryService,
        IInteractiveAuthorizationFlow authorizationFlow,
        IOAuthAuthorizationStateStore stateStore,
        ISecretProvider secretProvider,
        ILaunchTokenProtector launchTokenProtector,
        IConfiguredPipelineService pipelineService,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService,
        ILogger<InteractiveSourceAuthorizationService> logger)
    {
        _configurationRepository = configurationRepository;
        _discoveryService = discoveryService;
        _authorizationFlow = authorizationFlow;
        _stateStore = stateStore;
        _secretProvider = secretProvider;
        _launchTokenProtector = launchTokenProtector;
        _pipelineService = pipelineService;
        _auditService = auditService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public string BuildLaunchContextToken(Guid routeId) =>
        _launchTokenProtector.ProtectContext(routeId);

    public async Task<Uri> StartAsync(
        Guid sourceConnectionId,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            throw new ArgumentException("A redirect URI is required to start interactive authorization.", nameof(redirectUri));
        }

        var sourceConnection = await GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        var smartConfiguration = await DiscoverEndpointsAsync(sourceConnectionId, cancellationToken);
        var clientId = RequireClientId(sourceConnection);

        var source = new FhirSourceConfiguration(
            SourceType: MapSourceType(sourceConnection.SourceSystemType),
            Name: sourceConnection.Name,
            BaseUrl: sourceConnection.BaseUrl,
            TokenEndpoint: smartConfiguration.TokenEndpoint,
            ClientId: clientId,
            KeyId: null,
            PrivateKeyPem: null,
            Scopes: ApplyPatientSelection(
                sourceConnection.Authentication.Scopes,
                sourceConnection.Interactive?.PatientSelectionMethod),
            SourceConnectionId: sourceConnectionId,
            AuthorizationEndpoint: smartConfiguration.AuthorizationEndpoint);

        var authorizationUrl = await IssueAuthorizationAsync(
            source, sourceConnection, launch: null, routeId: null, requestedRedirectUri: redirectUri, cancellationToken);

        await RecordAuditAsync(sourceConnectionId, "InteractiveAuthorizationStarted", "Started",
            $"Interactive OAuth sign-in started for {sourceConnection.Name}.", cancellationToken);

        return authorizationUrl;
    }

    public async Task<Uri> StartEhrLaunchAsync(
        Guid sourceConnectionId,
        string issuer,
        string launch,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        return await StartEhrLaunchCoreAsync(sourceConnection, issuer, launch, redirectUri, routeId: null, cancellationToken);
    }

    public async Task<Uri> StartEhrLaunchFromContextAsync(
        string launchContext,
        string issuer,
        string launch,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        var context = _launchTokenProtector.UnprotectContext(launchContext)
            ?? throw new InvalidOperationException("The launch context is invalid or has been tampered with.");

        var (sourceConnection, routeId) = await ResolveRouteSourceAsync(context.RouteId, cancellationToken);
        return await StartEhrLaunchCoreAsync(sourceConnection, issuer, launch, redirectUri, routeId, cancellationToken);
    }

    private async Task<Uri> StartEhrLaunchCoreAsync(
        SourceConnection sourceConnection,
        string issuer,
        string launch,
        string redirectUri,
        Guid? routeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(launch))
        {
            throw new ArgumentException("An EHR launch requires both an issuer (iss) and a launch token.");
        }

        // The trusted-issuer allow-list is mandatory for EHR launch: validate the incoming iss BEFORE any redirect to
        // defeat token phishing. An empty allow-list means the source is not configured for EHR launch.
        var trustedIssuers = sourceConnection.Interactive?.TrustedIssuers ?? [];
        if (trustedIssuers.Length == 0 || !trustedIssuers.Any(trusted => IssuersMatch(trusted, issuer)))
        {
            throw new InvalidOperationException("The launch issuer (iss) is not in the source's trusted-issuer allow-list.");
        }

        var smartConfiguration = await DiscoverEndpointsAsync(sourceConnection.Id, cancellationToken);
        var clientId = RequireClientId(sourceConnection);

        var source = new FhirSourceConfiguration(
            SourceType: MapSourceType(sourceConnection.SourceSystemType),
            Name: sourceConnection.Name,
            // aud must equal the launch issuer for an EHR launch.
            BaseUrl: issuer,
            TokenEndpoint: smartConfiguration.TokenEndpoint,
            ClientId: clientId,
            KeyId: null,
            PrivateKeyPem: null,
            Scopes: sourceConnection.Authentication.Scopes,
            SourceConnectionId: sourceConnection.Id,
            AuthorizationEndpoint: smartConfiguration.AuthorizationEndpoint);

        var authorizationUrl = await IssueAuthorizationAsync(
            source, sourceConnection, launch, routeId, requestedRedirectUri: redirectUri, cancellationToken);

        await RecordAuditAsync(sourceConnection.Id, "EhrLaunchAuthorizationStarted", "Started",
            $"EHR launch started for {sourceConnection.Name} (iss {issuer}).", cancellationToken);

        return authorizationUrl;
    }

    // Shared tail of every start flow: mint a nonce, encrypt it as the OAuth state, build the authorize redirect, and
    // persist the pending authorization keyed by the nonce (single-use). Raw identifiers never leave the server.
    private async Task<Uri> IssueAuthorizationAsync(
        FhirSourceConfiguration source,
        SourceConnection sourceConnection,
        string? launch,
        Guid? routeId,
        string requestedRedirectUri,
        CancellationToken cancellationToken)
    {
        // Prefer a registered redirect URI (must match the EHR registration exactly); fall back to the request-derived one.
        var effectiveRedirectUri = sourceConnection.Interactive?.RedirectUris.FirstOrDefault() ?? requestedRedirectUri;

        var nonce = CreateNonce();
        var state = _launchTokenProtector.ProtectState(nonce);
        var request = _authorizationFlow.BuildAuthorizationRequest(source, effectiveRedirectUri, state, launch);

        await _stateStore.SaveAsync(
            nonce,
            new PendingAuthorization(
                source.SourceConnectionId!.Value,
                source.SourceType,
                sourceConnection.Name,
                request.CodeVerifier,
                effectiveRedirectUri,
                source.TokenEndpoint!,
                source.ClientId!,
                routeId),
            cancellationToken);

        return new Uri(request.AuthorizationUrl);
    }

    public async Task<InteractiveAuthorizationResult> CompleteAsync(
        string state,
        string authorizationCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state) || string.IsNullOrWhiteSpace(authorizationCode))
        {
            throw new ArgumentException("An authorization code and state are required to complete authorization.");
        }

        var nonce = _launchTokenProtector.UnprotectState(state)
            ?? throw new InvalidOperationException("The authorization state is invalid or has expired.");

        var pending = await _stateStore.TakeAsync(nonce, cancellationToken)
            ?? throw new InvalidOperationException("The authorization state is unknown or has already been used.");

        // Re-load the source to resolve confidential-client credentials for the token exchange. Secrets are resolved
        // here (not carried in the pending state) so they are never persisted in the short-lived authorization store.
        var sourceConnection = await GetSourceConnectionAsync(pending.SourceConnectionId, cancellationToken);
        var (clientSecret, privateKeyPem) = await ResolveClientCredentialsAsync(sourceConnection, cancellationToken);

        var source = new FhirSourceConfiguration(
            SourceType: pending.SourceType,
            Name: pending.SourceName,
            BaseUrl: null,
            TokenEndpoint: pending.TokenEndpoint,
            ClientId: pending.ClientId,
            KeyId: sourceConnection.Authentication.KeyId,
            PrivateKeyPem: privateKeyPem,
            Scopes: [],
            SourceConnectionId: pending.SourceConnectionId,
            ClientSecret: clientSecret);

        try
        {
            await _authorizationFlow.ExchangeAuthorizationCodeAsync(
                source, authorizationCode, pending.CodeVerifier, pending.RedirectUri, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Interactive authorization exchange failed for source {SourceConnectionId}.",
                pending.SourceConnectionId);

            await RecordAuditAsync(pending.SourceConnectionId, "InteractiveAuthorizationFailed", "Failed",
                exception.Message, cancellationToken);
            throw;
        }

        await RecordAuditAsync(pending.SourceConnectionId, "InteractiveAuthorizationCompleted", "Completed",
            $"Interactive OAuth sign-in completed for {pending.SourceName}.", cancellationToken);

        if (pending.RouteId is { } routeId)
        {
            await TriggerRouteRunAsync(pending.SourceConnectionId, routeId, cancellationToken);
        }

        return new InteractiveAuthorizationResult(pending.SourceConnectionId, pending.SourceName);
    }

    // After a launch completes, run every enabled pipeline route bound to the launched source — the launch establishes
    // the patient context once, so all configured resource types (Patient, Encounter, Observation, …) are pulled for
    // that patient in a single run. The stored token is patient-scoped, so each route flows the launched patient's
    // data through Source → Mapping → Destination. A run failure does not fail the sign-in (the token is already
    // stored and the run can be retried) — it is logged and audited.
    private async Task TriggerRouteRunAsync(Guid sourceConnectionId, Guid launchedRouteId, CancellationToken cancellationToken)
    {
        try
        {
            var routeIds = await ResolveEnabledRouteIdsForSourceAsync(sourceConnectionId, cancellationToken);
            if (routeIds.Length == 0)
            {
                // Fall back to the launched route so a launch always runs something even if resolution finds nothing.
                routeIds = [launchedRouteId];
            }

            var request = new StartConfiguredPipelineRunRequest(null, "epic-ehr-launch", null) { RouteIds = routeIds };
            await _pipelineService.StartAsync(request, cancellationToken);

            await RecordAuditAsync(sourceConnectionId, "EhrLaunchPipelineTriggered", "Started",
                $"{routeIds.Length} pipeline route(s) triggered by EHR launch for source {sourceConnectionId}.", cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "EHR-launch pipeline trigger failed for source {SourceConnectionId} (launched route {RouteId}).",
                sourceConnectionId,
                launchedRouteId);

            await RecordAuditAsync(sourceConnectionId, "EhrLaunchPipelineTriggerFailed", "Failed",
                exception.Message, cancellationToken);
        }
    }

    private async Task<SmartConfigurationDto> DiscoverEndpointsAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var smartConfiguration = await _discoveryService.DiscoverSmartConfigurationAsync(sourceConnectionId, cancellationToken);
        if (string.IsNullOrWhiteSpace(smartConfiguration.AuthorizationEndpoint) ||
            string.IsNullOrWhiteSpace(smartConfiguration.TokenEndpoint))
        {
            throw new InvalidOperationException(
                "The source does not advertise the authorization and token endpoints required for an interactive sign-in.");
        }

        return smartConfiguration;
    }

    private static string RequireClientId(SourceConnection sourceConnection) =>
        string.IsNullOrWhiteSpace(sourceConnection.Authentication.ClientId)
            ? throw new InvalidOperationException("The source connection has no client ID configured for interactive sign-in.")
            : sourceConnection.Authentication.ClientId!;

    private async Task<SourceConnection> GetSourceConnectionAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        return await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);
    }

    // Resolves the source connection that backs a pipeline route (route → mapping profile → source connection).
    private async Task<(SourceConnection Source, Guid RouteId)> ResolveRouteSourceAsync(
        Guid routeId,
        CancellationToken cancellationToken)
    {
        var route = await _configurationRepository.GetRouteAsync(routeId, cancellationToken)
            ?? throw new NotFoundException("ResourcePipelineRoute", routeId);

        var mapping = await _configurationRepository.GetMappingProfileAsync(route.MappingProfileId, cancellationToken)
            ?? throw new InvalidOperationException("The pipeline route is not associated with a mapping profile.");

        var source = await _configurationRepository.GetSourceConnectionAsync(mapping.SourceConnectionId, cancellationToken)
            ?? throw new NotFoundException("SourceConnection", mapping.SourceConnectionId);

        return (source, routeId);
    }

    // Resolves the ids of every enabled pipeline route whose mapping profile is bound to the given source connection.
    // Used to fan a single launch out across all resource types the source is configured for.
    private async Task<Guid[]> ResolveEnabledRouteIdsForSourceAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var routes = await _configurationRepository.GetRoutesAsync(cancellationToken);
        var mappings = await _configurationRepository.GetMappingProfilesAsync(cancellationToken);
        var sourceMappingIds = mappings
            .Where(mapping => mapping.SourceConnectionId == sourceConnectionId)
            .Select(mapping => mapping.Id)
            .ToHashSet();

        return routes
            .Where(route => route.IsEnabled && sourceMappingIds.Contains(route.MappingProfileId))
            .Select(route => route.Id)
            .ToArray();
    }

    // Resolves the source's confidential-client credentials (from the secret store) for the token exchange. Returns
    // (null, null) for a public client, which then authenticates with PKCE alone.
    private async Task<(string? ClientSecret, string? PrivateKeyPem)> ResolveClientCredentialsAsync(
        SourceConnection sourceConnection,
        CancellationToken cancellationToken)
    {
        string? clientSecret = null;
        if (sourceConnection.Authentication.ClientSecret is not null)
        {
            clientSecret = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.ClientSecret, cancellationToken);
        }

        string? privateKeyPem = null;
        if (sourceConnection.Authentication.PrivateKey is not null)
        {
            privateKeyPem = await _secretProvider.GetSecretAsync(sourceConnection.Authentication.PrivateKey, cancellationToken);
        }

        return (clientSecret, privateKeyPem);
    }

    // Adds the patient-context scope implied by the selection method, without duplicating an already-configured scope.
    private static string[] ApplyPatientSelection(string[] scopes, PatientSelectionMethod? method)
    {
        var patientScope = method switch
        {
            PatientSelectionMethod.LaunchPatient => "launch/patient",
            PatientSelectionMethod.AppDrivenSearch => "user/Patient.search",
            _ => null
        };

        if (patientScope is null || scopes.Contains(patientScope, StringComparer.Ordinal))
        {
            return scopes;
        }

        return [.. scopes, patientScope];
    }

    // Compares two issuers ignoring a trailing slash and case (FHIR base URLs are compared case-insensitively).
    private static bool IssuersMatch(string trusted, string incoming) =>
        string.Equals(trusted.TrimEnd('/'), incoming.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    // A cryptographically random, URL-safe nonce — the single-use key into the pending-authorization store. It is
    // encrypted into the OAuth state before being sent to the EHR.
    private static string CreateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    // Vendor axis (SourceSystemType → RuntimeSourceType); unrelated to the application-type axis.
    private static RuntimeSourceType MapSourceType(SourceSystemType sourceSystemType) => sourceSystemType switch
    {
        SourceSystemType.Sample => RuntimeSourceType.Sample,
        SourceSystemType.Epic => RuntimeSourceType.Epic,
        SourceSystemType.Cerner => RuntimeSourceType.Cerner,
        SourceSystemType.Allscripts => RuntimeSourceType.Allscripts,
        SourceSystemType.GenericFhir => RuntimeSourceType.GenericFhir,
        SourceSystemType.Athenahealth => RuntimeSourceType.GenericFhir,
        SourceSystemType.Healow => RuntimeSourceType.Healow,
        SourceSystemType.MeditechGreenfield => RuntimeSourceType.MeditechGreenfield,
        _ => RuntimeSourceType.GenericFhir
    };

    private Task RecordAuditAsync(
        Guid sourceConnectionId,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                sourceConnectionId,
                null,
                null,
                null,
                action,
                status,
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);
    }
}
