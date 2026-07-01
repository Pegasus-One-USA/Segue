using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Orchestrates the interactive OAuth sign-in for a source connection. It resolves the source, discovers its SMART
/// authorization/token endpoints on demand (so the endpoints need not be persisted), starts the authorization-code
/// + PKCE flow, and completes it from the callback — persisting the token via the interactive flow so later pipeline
/// runs read it back. Mirrors <see cref="SourceConnectionTestService"/>'s resolution pattern.
/// </summary>
public sealed class InteractiveSourceAuthorizationService : IInteractiveSourceAuthorizationService
{
    private readonly ITenantConfigurationRepository _tenantRepository;
    private readonly ISourceCapabilityDiscoveryService _discoveryService;
    private readonly IInteractiveAuthorizationFlow _authorizationFlow;
    private readonly IOAuthAuthorizationStateStore _stateStore;
    private readonly ISecretProvider _secretProvider;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<InteractiveSourceAuthorizationService> _logger;

    public InteractiveSourceAuthorizationService(
        ITenantConfigurationRepository tenantRepository,
        ISourceCapabilityDiscoveryService discoveryService,
        IInteractiveAuthorizationFlow authorizationFlow,
        IOAuthAuthorizationStateStore stateStore,
        ISecretProvider secretProvider,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService,
        ILogger<InteractiveSourceAuthorizationService> logger)
    {
        _tenantRepository = tenantRepository;
        _discoveryService = discoveryService;
        _authorizationFlow = authorizationFlow;
        _stateStore = stateStore;
        _secretProvider = secretProvider;
        _auditService = auditService;
        _currentUserService = currentUserService;
        _logger = logger;
    }

    public async Task<Uri> StartAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            throw new ArgumentException("A redirect URI is required to start interactive authorization.", nameof(redirectUri));
        }

        var sourceConnection = await GetSourceConnectionAsync(tenantId, sourceConnectionId, cancellationToken);

        var smartConfiguration = await _discoveryService.DiscoverSmartConfigurationAsync(
            tenantId, sourceConnectionId, cancellationToken);

        if (string.IsNullOrWhiteSpace(smartConfiguration.AuthorizationEndpoint) ||
            string.IsNullOrWhiteSpace(smartConfiguration.TokenEndpoint))
        {
            throw new InvalidOperationException(
                "The source does not advertise the authorization and token endpoints required for an interactive sign-in.");
        }

        var clientId = sourceConnection.Authentication.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("The source connection has no client ID configured for interactive sign-in.");
        }

        // Prefer a registered redirect URI (must match Epic exactly); fall back to the caller's request-derived one.
        var effectiveRedirectUri = sourceConnection.Interactive?.RedirectUris.FirstOrDefault() ?? redirectUri;

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
            TenantId: tenantId,
            SourceConnectionId: sourceConnectionId,
            AuthorizationEndpoint: smartConfiguration.AuthorizationEndpoint);

        var state = CreateState();
        var request = _authorizationFlow.BuildAuthorizationRequest(source, effectiveRedirectUri, state);

        await _stateStore.SaveAsync(
            state,
            new PendingAuthorization(
                tenantId,
                sourceConnectionId,
                source.SourceType,
                sourceConnection.Name,
                request.CodeVerifier,
                effectiveRedirectUri,
                smartConfiguration.TokenEndpoint!,
                clientId!),
            cancellationToken);

        await RecordAuditAsync(
            tenantId,
            sourceConnectionId,
            "InteractiveAuthorizationStarted",
            "Started",
            $"Interactive OAuth sign-in started for {sourceConnection.Name}.",
            cancellationToken);

        return new Uri(request.AuthorizationUrl);
    }

    public async Task<Uri> StartEhrLaunchAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        string issuer,
        string launch,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(launch))
        {
            throw new ArgumentException("An EHR launch requires both an issuer (iss) and a launch token.");
        }

        var sourceConnection = await GetSourceConnectionAsync(tenantId, sourceConnectionId, cancellationToken);

        // The trusted-issuer allow-list is mandatory for EHR launch: validate the incoming iss BEFORE any redirect to
        // defeat token phishing. An empty allow-list means the source is not configured for EHR launch.
        var trustedIssuers = sourceConnection.Interactive?.TrustedIssuers ?? [];
        if (trustedIssuers.Length == 0 || !trustedIssuers.Any(trusted => IssuersMatch(trusted, issuer)))
        {
            throw new InvalidOperationException(
                "The launch issuer (iss) is not in the source's trusted-issuer allow-list.");
        }

        var smartConfiguration = await _discoveryService.DiscoverSmartConfigurationAsync(
            tenantId, sourceConnectionId, cancellationToken);

        if (string.IsNullOrWhiteSpace(smartConfiguration.AuthorizationEndpoint) ||
            string.IsNullOrWhiteSpace(smartConfiguration.TokenEndpoint))
        {
            throw new InvalidOperationException(
                "The source does not advertise the authorization and token endpoints required for an EHR launch.");
        }

        var clientId = sourceConnection.Authentication.ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("The source connection has no client ID configured for EHR launch.");
        }

        var effectiveRedirectUri = sourceConnection.Interactive?.RedirectUris.FirstOrDefault() ?? redirectUri;

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
            TenantId: tenantId,
            SourceConnectionId: sourceConnectionId,
            AuthorizationEndpoint: smartConfiguration.AuthorizationEndpoint);

        var state = CreateState();
        var request = _authorizationFlow.BuildAuthorizationRequest(source, effectiveRedirectUri, state, launch);

        await _stateStore.SaveAsync(
            state,
            new PendingAuthorization(
                tenantId,
                sourceConnectionId,
                source.SourceType,
                sourceConnection.Name,
                request.CodeVerifier,
                effectiveRedirectUri,
                smartConfiguration.TokenEndpoint!,
                clientId!),
            cancellationToken);

        await RecordAuditAsync(
            tenantId,
            sourceConnectionId,
            "EhrLaunchAuthorizationStarted",
            "Started",
            $"EHR launch started for {sourceConnection.Name} (iss {issuer}).",
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

        var pending = await _stateStore.TakeAsync(state, cancellationToken)
            ?? throw new InvalidOperationException("The authorization state is unknown or has already been used.");

        // Re-load the source to resolve confidential-client credentials for the token exchange. Secrets are resolved
        // here (not carried in the pending state) so they are never persisted in the short-lived authorization store.
        var sourceConnection = await GetSourceConnectionAsync(pending.TenantId, pending.SourceConnectionId, cancellationToken);
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
            TenantId: pending.TenantId,
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
                "Interactive authorization exchange failed for tenant {TenantId}, source {SourceConnectionId}.",
                pending.TenantId,
                pending.SourceConnectionId);

            await RecordAuditAsync(
                pending.TenantId,
                pending.SourceConnectionId,
                "InteractiveAuthorizationFailed",
                "Failed",
                exception.Message,
                cancellationToken);
            throw;
        }

        await RecordAuditAsync(
            pending.TenantId,
            pending.SourceConnectionId,
            "InteractiveAuthorizationCompleted",
            "Completed",
            $"Interactive OAuth sign-in completed for {pending.SourceName}.",
            cancellationToken);

        return new InteractiveAuthorizationResult(pending.TenantId, pending.SourceConnectionId, pending.SourceName);
    }

    private async Task<SourceConnection> GetSourceConnectionAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var tenant = await _tenantRepository.GetByIdAsync(tenantId, cancellationToken)
            ?? throw new NotFoundException("Tenant", tenantId);

        return tenant.SourceConnections.FirstOrDefault(x => x.Id == sourceConnectionId)
            ?? throw new NotFoundException("SourceConnection", sourceConnectionId);
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

    // A cryptographically random, URL-safe state value — the CSRF token linking the callback back to this sign-in.
    private static string CreateState()
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
        Guid tenantId,
        Guid sourceConnectionId,
        string action,
        string status,
        string message,
        CancellationToken cancellationToken)
    {
        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                tenantId,
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
