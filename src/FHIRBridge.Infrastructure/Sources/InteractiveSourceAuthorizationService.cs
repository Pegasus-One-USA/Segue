using System.Security.Cryptography;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Infrastructure.Workflows;
using FHIRBridge.Runtime.Application.Abstractions.Auth;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    private readonly IUserActivityAuditService _userActivityAuditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ILogger<InteractiveSourceAuthorizationService> _logger;

    // Scenario B (optional): when the graph-execution flag is on for a source, the launch runs its persisted
    // workflow graph instead of the flat route path. Left null (flag off / not composed) => route path only.
    private readonly IRankedWorkflowOrchestrator? _workflowOrchestrator;
    private readonly ILaunchWorkflowResolver? _launchWorkflowResolver;
    private readonly IWorkflowDefinitionStore? _workflowDefinitionStore;
    private readonly WorkflowGraphExecutionOptions _graphExecutionOptions;

    public InteractiveSourceAuthorizationService(
        IConfigurationRepository configurationRepository,
        ISourceCapabilityDiscoveryService discoveryService,
        IInteractiveAuthorizationFlow authorizationFlow,
        IOAuthAuthorizationStateStore stateStore,
        ISecretProvider secretProvider,
        ILaunchTokenProtector launchTokenProtector,
        IConfiguredPipelineService pipelineService,
        IOperationalAuditService auditService,
        IUserActivityAuditService userActivityAuditService,
        ICurrentUserService currentUserService,
        ILogger<InteractiveSourceAuthorizationService> logger,
        IRankedWorkflowOrchestrator? workflowOrchestrator = null,
        ILaunchWorkflowResolver? launchWorkflowResolver = null,
        IWorkflowDefinitionStore? workflowDefinitionStore = null,
        IOptions<WorkflowGraphExecutionOptions>? graphExecutionOptions = null)
    {
        _configurationRepository = configurationRepository;
        _discoveryService = discoveryService;
        _authorizationFlow = authorizationFlow;
        _stateStore = stateStore;
        _secretProvider = secretProvider;
        _launchTokenProtector = launchTokenProtector;
        _pipelineService = pipelineService;
        _auditService = auditService;
        _userActivityAuditService = userActivityAuditService;
        _currentUserService = currentUserService;
        _logger = logger;
        _workflowOrchestrator = workflowOrchestrator;
        _launchWorkflowResolver = launchWorkflowResolver;
        _workflowDefinitionStore = workflowDefinitionStore;
        _graphExecutionOptions = graphExecutionOptions?.Value ?? new WorkflowGraphExecutionOptions();
    }

    public string BuildLaunchContextToken(Guid routeId, Guid? ehrEndpointId = null) =>
        _launchTokenProtector.ProtectContext(routeId, ehrEndpointId);

    public string BuildWorkflowLaunchContextToken(Guid workflowId, Guid? ehrEndpointId = null) =>
        _launchTokenProtector.ProtectWorkflowContext(workflowId, ehrEndpointId);

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
            source, sourceConnection, launch: null, routeId: null, workflowId: null, requestedRedirectUri: redirectUri, cancellationToken);

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
        try
        {
            var sourceConnection = await GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
            return await StartEhrLaunchCoreAsync(sourceConnection, issuer, launch, redirectUri, routeId: null, workflowId: null, cancellationToken);
        }
        catch (Exception exception)
        {
            await RecordAccessAttemptFailureAsync("EhrLaunch", issuer, sourceConnectionId, exception, cancellationToken);
            throw;
        }
    }

    public async Task<Uri> StartEhrLaunchFromContextAsync(
        string launchContext,
        string issuer,
        string launch,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        // Tracks the source connection as soon as it resolves, so a later failure (e.g. untrusted issuer) is audited
        // against the real connection rather than logged as an anonymous/unresolved attempt.
        Guid? resolvedSourceConnectionId = null;
        try
        {
            var context = _launchTokenProtector.UnprotectContext(launchContext)
                ?? throw new InvalidOperationException("The launch context is invalid or has been tampered with.");

            // Workflow launch: the graph's source node names the connection to OAuth against and validate iss for.
            if (context.WorkflowId is { } workflowId)
            {
                var workflowSource = await ResolveWorkflowSourceAsync(workflowId, cancellationToken);
                resolvedSourceConnectionId = workflowSource.Id;
                return await StartEhrLaunchCoreAsync(
                    workflowSource, issuer, launch, redirectUri, routeId: null, workflowId: workflowId, cancellationToken);
            }

            if (context.RouteId is { } contextRouteId)
            {
                var (sourceConnection, routeId) = await ResolveRouteSourceAsync(contextRouteId, cancellationToken);
                resolvedSourceConnectionId = sourceConnection.Id;
                return await StartEhrLaunchCoreAsync(
                    sourceConnection, issuer, launch, redirectUri, routeId, workflowId: null, cancellationToken);
            }

            throw new InvalidOperationException("The launch context does not reference a route or a workflow.");
        }
        catch (Exception exception)
        {
            await RecordAccessAttemptFailureAsync("EhrLaunchFromContext", issuer, resolvedSourceConnectionId, exception, cancellationToken);
            throw;
        }
    }


    public async Task<Uri> StartInteractiveFromContextAsync(
      string launchContext,
      string redirectUri,
      CancellationToken cancellationToken)
    {
        // Tracks the source connection as soon as it resolves, so a later failure is audited against the real
        // connection rather than logged as an anonymous/unresolved attempt.
        Guid? resolvedSourceConnectionId = null;
        try
        {
            var context = _launchTokenProtector.UnprotectContext(launchContext)
                ?? throw new InvalidOperationException("The launch context is invalid or has been tampered with.");

            SourceConnection sourceConnection;
            Guid? routeId = null;
            Guid? workflowId = null;
            if (context.WorkflowId is { } wf)
            {
                sourceConnection = await ResolveWorkflowSourceAsync(wf, cancellationToken);
                workflowId = wf;
            }
            else if (context.RouteId is { } rt)
            {
                (sourceConnection, routeId) = await ResolveRouteSourceAsync(rt, cancellationToken);
            }
            else
            {
                throw new InvalidOperationException("The launch context does not reference a route or a workflow.");
            }

            resolvedSourceConnectionId = sourceConnection.Id;

            // This entry point is for the directly-opened flows (provider standalone / patient). An EHR-launch source has
            // no patient context without an iss/launch token, so it must use the /oauth/launch entry instead.
            if (sourceConnection.ApplicationType is ApplicationType.EhrLaunch)
            {
                throw new InvalidOperationException(
                    "This source is configured for EHR launch; open it from the EHR (which supplies iss + launch) rather than directly.");
            }

            // A hospital/organization selection carried through the launch context resolves to the actual endpoint
            // to launch against instead of the source connection's own configured base URL. Missing/unknown ids are
            // treated the same as "no selection" — fall back to the connection's own base URL.
            var ehrEndpoint = context.EhrEndpointId is { } ehrEndpointId
                ? await _configurationRepository.GetEhrEndpointAsync(ehrEndpointId, cancellationToken)
                : null;
            var baseUrl = ehrEndpoint?.FhirBaseUrl ?? sourceConnection.BaseUrl;

            var smartConfiguration = await DiscoverEndpointsAsync(sourceConnection.Id, cancellationToken, baseUrl);
            var clientId = RequireClientId(sourceConnection);

            var persistedScopes = sourceConnection.Authentication.Scopes;
            var patientSelectionMethod = sourceConnection.Interactive?.PatientSelectionMethod;
            var resolvedScopes = ApplyPatientSelection(persistedScopes, patientSelectionMethod);
            _logger.LogInformation(
                "[Step 2/6] StartInteractiveFromContextAsync: sourceConnectionId={SourceConnectionId} " +
                "applicationType={ApplicationType} persistedScopes=[{PersistedScopes}] " +
                "patientSelectionMethod={PatientSelectionMethod} resolvedScopes=[{ResolvedScopes}] launch=null",
                sourceConnection.Id, sourceConnection.ApplicationType, string.Join(' ', persistedScopes),
                patientSelectionMethod, string.Join(' ', resolvedScopes));

            var source = new FhirSourceConfiguration(
                SourceType: MapSourceType(sourceConnection.SourceSystemType),
                Name: sourceConnection.Name,
                BaseUrl: baseUrl,
                TokenEndpoint: smartConfiguration.TokenEndpoint,
                ClientId: clientId,
                KeyId: null,
                PrivateKeyPem: null,
                Scopes: resolvedScopes,
                SourceConnectionId: sourceConnection.Id,
                AuthorizationEndpoint: smartConfiguration.AuthorizationEndpoint);

            var authorizationUrl = await IssueAuthorizationAsync(
                source, sourceConnection, launch: null, routeId, workflowId, requestedRedirectUri: redirectUri, cancellationToken);

            await RecordAuditAsync(sourceConnection.Id, "InteractiveAuthorizationStarted", "Started",
                ehrEndpoint is null
                    ? $"Standalone/patient OAuth sign-in started for {sourceConnection.Name}."
                    : $"Standalone/patient OAuth sign-in started for {sourceConnection.Name} (hospital endpoint {ehrEndpoint.Name}).",
                cancellationToken);

            return authorizationUrl;
        }
        catch (Exception exception)
        {
            await RecordAccessAttemptFailureAsync("InteractiveFromContext", issuer: null, resolvedSourceConnectionId, exception, cancellationToken);
            throw;
        }
    }
    public async Task<Uri> StartStandaloneFromContextAsync(
        string launchContext,
        string redirectUri,
        CancellationToken cancellationToken)
    {
        // Tracks the source connection as soon as it resolves, so a later failure is audited against the real
        // connection rather than logged as an anonymous/unresolved attempt.
        Guid? resolvedSourceConnectionId = null;
        try
        {
            if (string.IsNullOrWhiteSpace(redirectUri))
            {
                throw new ArgumentException("A redirect URI is required to start interactive authorization.", nameof(redirectUri));
            }

            var context = _launchTokenProtector.UnprotectContext(launchContext)
                ?? throw new InvalidOperationException("The launch context is invalid or has been tampered with.");

            // A hospital/organization selection carried through the launch context resolves to the actual endpoint
            // to launch against instead of the source connection's own configured base URL. Missing/unknown ids are
            // treated the same as "no selection" — fall back to the connection's own base URL rather than fail the
            // launch outright, since the picker is owned by a third-party app outside this service's control.
            var ehrEndpoint = context.EhrEndpointId is { } ehrEndpointId
                ? await _configurationRepository.GetEhrEndpointAsync(ehrEndpointId, cancellationToken)
                : null;

            // Workflow launch: the graph's source node names the connection to OAuth against.
            if (context.WorkflowId is { } workflowId)
            {
                var workflowSource = await ResolveWorkflowSourceAsync(workflowId, cancellationToken);
                resolvedSourceConnectionId = workflowSource.Id;
                return await StartStandaloneCoreAsync(workflowSource, redirectUri, routeId: null, workflowId, ehrEndpoint, cancellationToken);
            }

            if (context.RouteId is { } contextRouteId)
            {
                var (sourceConnection, routeId) = await ResolveRouteSourceAsync(contextRouteId, cancellationToken);
                resolvedSourceConnectionId = sourceConnection.Id;
                return await StartStandaloneCoreAsync(sourceConnection, redirectUri, routeId, workflowId: null, ehrEndpoint, cancellationToken);
            }

            throw new InvalidOperationException("The launch context does not reference a route or a workflow.");
        }
        catch (Exception exception)
        {
            await RecordAccessAttemptFailureAsync("StandaloneFromContext", issuer: null, resolvedSourceConnectionId, exception, cancellationToken);
            throw;
        }
    }

    private async Task<Uri> StartStandaloneCoreAsync(
        SourceConnection sourceConnection,
        string redirectUri,
        Guid? routeId,
        Guid? workflowId,
        EhrEndpoint? ehrEndpoint,
        CancellationToken cancellationToken)
    {
        // A resolved hospital/organization endpoint overrides the connection's own configured base URL — discovery
        // must fetch SMART configuration from that SAME url, or the authorize/token endpoints returned would belong
        // to the wrong Epic instance while the base URL/aud points at the right one.
        var baseUrl = ehrEndpoint?.FhirBaseUrl ?? sourceConnection.BaseUrl;
        var smartConfiguration = await DiscoverEndpointsAsync(sourceConnection.Id, cancellationToken, baseUrl);
        var clientId = RequireClientId(sourceConnection);

        var persistedScopes = sourceConnection.Authentication.Scopes;
        var patientSelectionMethod = sourceConnection.Interactive?.PatientSelectionMethod;
        var resolvedScopes = ApplyPatientSelection(persistedScopes, patientSelectionMethod);
        _logger.LogInformation(
            "[Step 2/6] StartStandaloneCoreAsync: sourceConnectionId={SourceConnectionId} " +
            "applicationType={ApplicationType} persistedScopes=[{PersistedScopes}] " +
            "patientSelectionMethod={PatientSelectionMethod} resolvedScopes=[{ResolvedScopes}] launch=null",
            sourceConnection.Id, sourceConnection.ApplicationType, string.Join(' ', persistedScopes),
            patientSelectionMethod, string.Join(' ', resolvedScopes));

        var source = new FhirSourceConfiguration(
            SourceType: MapSourceType(sourceConnection.SourceSystemType),
            Name: sourceConnection.Name,
            // No launch issuer in a standalone sign-in — the (possibly hospital-overridden) FHIR base URL is the audience.
            BaseUrl: baseUrl,
            TokenEndpoint: smartConfiguration.TokenEndpoint,
            ClientId: clientId,
            KeyId: null,
            PrivateKeyPem: null,
            Scopes: resolvedScopes,
            SourceConnectionId: sourceConnection.Id,
            AuthorizationEndpoint: smartConfiguration.AuthorizationEndpoint);

        var authorizationUrl = await IssueAuthorizationAsync(
            source, sourceConnection, launch: null, routeId, workflowId, requestedRedirectUri: redirectUri, cancellationToken);

        await RecordAuditAsync(sourceConnection.Id, "StandaloneAuthorizationStarted", "Started",
            ehrEndpoint is null
                ? $"Provider-standalone sign-in started for {sourceConnection.Name}."
                : $"Provider-standalone sign-in started for {sourceConnection.Name} (hospital endpoint {ehrEndpoint.Name}).",
            cancellationToken);

        return authorizationUrl;
    }


    public async Task<ApplicationType?> GetWorkflowApplicationTypeAsync(Guid workflowId, CancellationToken cancellationToken) =>
    (await ResolveWorkflowSourceAsync(workflowId, cancellationToken)).ApplicationType;

    public async Task<ApplicationType?> GetRouteApplicationTypeAsync(Guid routeId, CancellationToken cancellationToken) =>
        (await ResolveRouteSourceAsync(routeId, cancellationToken)).Source.ApplicationType;

    private async Task<Uri> StartEhrLaunchCoreAsync(
        SourceConnection sourceConnection,
        string issuer,
        string launch,
        string redirectUri,
        Guid? routeId,
        Guid? workflowId,
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
            source, sourceConnection, launch, routeId, workflowId, requestedRedirectUri: redirectUri, cancellationToken);

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
        Guid? workflowId,
        string requestedRedirectUri,
        CancellationToken cancellationToken)
    {
        // Prefer a registered redirect URI (must match the EHR registration exactly); fall back to the request-derived one.
        var effectiveRedirectUri = sourceConnection.Interactive?.RedirectUris.FirstOrDefault() ?? requestedRedirectUri;
        _logger.LogInformation(
            "[Step 3/6] IssueAuthorizationAsync: sourceConnectionId={SourceConnectionId} " +
            "configuredRedirectUri={ConfiguredRedirectUri} requestedRedirectUri={RequestedRedirectUri} " +
            "effectiveRedirectUri={EffectiveRedirectUri} launch={Launch}",
            sourceConnection.Id, sourceConnection.Interactive?.RedirectUris.FirstOrDefault(), requestedRedirectUri,
            effectiveRedirectUri, launch);

        var nonce = CreateNonce();
        var state = _launchTokenProtector.ProtectState(nonce);
        var request = _authorizationFlow.BuildAuthorizationRequest(source, effectiveRedirectUri, state, launch);
        _logger.LogInformation(
            "[Step 4/6] BuildAuthorizationRequest produced: sourceConnectionId={SourceConnectionId} authorizationUrl={AuthorizationUrl}",
            sourceConnection.Id, request.AuthorizationUrl);

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
                routeId,
                workflowId,
                // Carries whichever base URL this authorize request actually used (the connection's own, or a
                // resolved EhrEndpoint override) through to CompleteAsync — the token exchange there builds its own
                // FhirSourceConfiguration from scratch and has no other way to learn which URL was used.
                ResolvedBaseUrl: source.BaseUrl,
                HasLaunchContext: launch is not null),
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

        _logger.LogInformation(
            "[Step 5/6] CompleteAsync: sourceConnectionId={SourceConnectionId} sourceName={SourceName} " +
            "redirectUri={RedirectUri} routeId={RouteId} workflowId={WorkflowId} hasLaunchContext={HasLaunchContext}",
            pending.SourceConnectionId, pending.SourceName, pending.RedirectUri, pending.RouteId, pending.WorkflowId,
            pending.HasLaunchContext);

        // Re-load the source to resolve confidential-client credentials for the token exchange. Secrets are resolved
        // here (not carried in the pending state) so they are never persisted in the short-lived authorization store.
        var sourceConnection = await GetSourceConnectionAsync(pending.SourceConnectionId, cancellationToken);
        var (clientSecret, privateKeyPem) = await ResolveClientCredentialsAsync(sourceConnection, cancellationToken);

        var source = new FhirSourceConfiguration(
            SourceType: pending.SourceType,
            Name: pending.SourceName,
            // Carried through from the authorize step so the token save below records the SAME base URL that was
            // actually used (the connection's own, or a resolved hospital/organization EhrEndpoint override).
            BaseUrl: pending.ResolvedBaseUrl,
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

        // Business-level event — the one moment in this flow worth surfacing on the Activity Feed. The many
        // "Started"/"Rejected"/"Triggered" actions elsewhere in this file stay operational-only (RecordAuditAsync):
        // they fire for anonymous third-party EHR-launch traffic too, which isn't a business event a portal user
        // would recognize as "something I did."
        var user = _currentUserService.CurrentUser;
        var userId = Guid.TryParse(user.ExternalUserId, out var parsedUserId) ? parsedUserId : (Guid?)null;
        await _userActivityAuditService.RecordAsync(
            new RecordUserActivityRequest(
                UserId: userId,
                UserEmail: user.AuditName,
                Category: UserActivityCategories.Authentication,
                Activity: $"OAuth connection authorized for {pending.SourceName}",
                Status: UserActivityStatuses.Success,
                EntityName: pending.SourceName,
                EntityId: pending.SourceConnectionId,
                IpAddress: user.IpAddress,
                UserAgent: user.UserAgent,
                CorrelationId: user.CorrelationId,
                Module: "SourceConnection",
                Action: "OAuthAuthorized"),
            cancellationToken);

        Guid? workflowRunId = null;
        var workflowRunFailed = false;
        // A workflow-bound sign-in with no launch context (Standalone/patient-standalone — no `launch` token was
        // ever issued, see IssueAuthorizationAsync) has nothing for this convenience run to search with: it fires
        // before the caller's own page has even reloaded, so it always hits Epic's "requires demographics or _id"
        // business rule. Firing it anyway costs a real Epic API call and an always-failing WorkflowRun row for zero
        // benefit — the caller (e.g. Demo_TestApp's LaunchStandaloneProviderComponent) is expected to run its own
        // criteria-scoped fetch once it reloads. An EHR launch (HasLaunchContext true) keeps firing it as before,
        // since Epic hands over a real patient context there and the run has a genuine chance of finding data.
        var skipWorkflowTrigger = pending.WorkflowId is not null && !pending.HasLaunchContext;
        _logger.LogInformation(
            "[Step 5/6] CompleteAsync token exchange succeeded for {SourceConnectionId}; skipWorkflowTrigger={SkipWorkflowTrigger}",
            pending.SourceConnectionId, skipWorkflowTrigger);
        if (pending.RouteId is { } routeId)
        {
            // Deliberately NOT the request token: the callback's caller is the provider's browser, which may
            // disconnect (tab closed, redirect) while the run is still pulling from the EHR. The run must not
            // die with the connection.
            await TriggerRouteRunAsync(pending.SourceConnectionId, routeId, CancellationToken.None);
        }
        else if (pending.WorkflowId is { } workflowId && !skipWorkflowTrigger)
        {
            // Same reasoning as the route path above — the run must survive the browser disconnecting/redirecting
            // while it's still in flight, so it uses CancellationToken.None rather than the request's token.
            var triggerResult = await TriggerWorkflowRunAsync(pending.SourceConnectionId, workflowId, CancellationToken.None);
            workflowRunId = triggerResult.WorkflowRunId;
            workflowRunFailed = triggerResult.Failed;
        }

        // Redirect back to the third-party app whenever a run was attempted (success or failure) and a redirect
        // URI is configured — only a plain sign-in with no bound workflow (nothing attempted) falls through to
        // the bare JSON response, since there is nothing for the caller to fetch or be told about either way. A
        // skipped-on-purpose workflow trigger (see skipWorkflowTrigger above) still redirects: something IS bound,
        // the caller still needs the round trip back to run its own fetch, we just chose not to attempt the
        // convenience run ourselves.
        var postLaunchRedirectUri = workflowRunId is not null || workflowRunFailed || skipWorkflowTrigger
            ? sourceConnection.Interactive?.PostLaunchRedirectUri
            : null;

        return new InteractiveAuthorizationResult(
            pending.SourceConnectionId, pending.SourceName, workflowRunId, postLaunchRedirectUri, workflowRunFailed,
            WorkflowRunSkipped: skipWorkflowTrigger);
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
            // Scenario B: when the graph-execution flag is on for this source and a persisted graph resolves, the
            // launch runs that graph (make.com-style) instead of the flat route path. Falls back to the route path
            // if the flag is off, the graph engine isn't composed, or no graph could be projected for the source.
            if (await TryTriggerGraphRunAsync(sourceConnectionId, cancellationToken))
            {
                return;
            }

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

    // Scenario B graph path. Returns true when the launch was handled by running the source's persisted workflow
    // graph. Returns false to defer to the route path when the flag is off, the graph engine isn't composed, or no
    // graph could be projected. A graph run that throws propagates to TriggerRouteRunAsync's catch (audited, and the
    // sign-in still succeeds since the token is already stored).
    private async Task<bool> TryTriggerGraphRunAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        if (_workflowOrchestrator is null
            || _launchWorkflowResolver is null
            || !_graphExecutionOptions.IsEnabledForSource(sourceConnectionId))
        {
            return false;
        }

        var workflow = await _launchWorkflowResolver.ResolveForSourceAsync(sourceConnectionId, cancellationToken);
        if (workflow is null)
        {
            return false;
        }

        var context = new WorkflowExecutionContext(
            Guid.NewGuid(),
            $"ehr-launch:{sourceConnectionId:N}",
            triggeredBy: $"source:{sourceConnectionId:N}",
            triggerType: "InteractiveLaunch");
        var result = await _workflowOrchestrator.ExecuteAsync(workflow, context, cancellationToken);

        await RecordAuditAsync(
            sourceConnectionId,
            "EhrLaunchGraphTriggered",
            "Started",
            $"Workflow graph '{workflow.Name}' ({result.WorkflowRun.NodeRuns.Count} node run(s)) triggered by EHR launch for source {sourceConnectionId}.",
            cancellationToken);

        return true;
    }

    // Workflow launch: run the referenced workflow graph once the token is stored (mirrors TriggerRouteRunAsync;
    // a run failure is audited but does not fail the sign-in, since the token is already persisted). Returns
    // Failed=true (not just a null run id) on failure so the caller can still redirect the browser back to the
    // third-party app with an error marker, instead of leaving it stranded on this endpoint's bare JSON response.
    private async Task<WorkflowRunTriggerResult> TriggerWorkflowRunAsync(Guid sourceConnectionId, Guid workflowId, CancellationToken cancellationToken)
    {
        try
        {
            if (_workflowOrchestrator is null || _workflowDefinitionStore is null)
            {
                throw new InvalidOperationException("Workflow execution is not composed.");
            }

            var workflow = await _workflowDefinitionStore.GetAsync(workflowId, cancellationToken)
                ?? throw new NotFoundException("WorkflowDefinition", workflowId);

            var context = new WorkflowExecutionContext(
                Guid.NewGuid(),
                $"ehr-launch:workflow:{workflowId:N}",
                triggeredBy: $"source:{sourceConnectionId:N}",
                triggerType: "InteractiveLaunch");
            var result = await _workflowOrchestrator.ExecuteAsync(workflow, context, cancellationToken);

            await RecordAuditAsync(sourceConnectionId, "EhrLaunchWorkflowTriggered", "Started",
                $"Workflow '{workflow.Name}' ({result.WorkflowRun.NodeRuns.Count} node run(s)) triggered by EHR launch for source {sourceConnectionId}.",
                cancellationToken);

            return new WorkflowRunTriggerResult(result.WorkflowRun.Id, Failed: false);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "EHR-launch workflow run failed for source {SourceConnectionId} (workflow {WorkflowId}).",
                sourceConnectionId,
                workflowId);

            await RecordAuditAsync(sourceConnectionId, "EhrLaunchWorkflowTriggerFailed", "Failed",
                exception.Message, cancellationToken);

            return new WorkflowRunTriggerResult(WorkflowRunId: null, Failed: true);
        }
    }

    private readonly record struct WorkflowRunTriggerResult(Guid? WorkflowRunId, bool Failed);

    // Resolves the source connection a workflow launches against — the (first) source node's referenced sourceConnectionId.
    private async Task<SourceConnection> ResolveWorkflowSourceAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        if (_workflowDefinitionStore is null)
        {
            throw new InvalidOperationException("Workflow launch is not available (workflow store not composed).");
        }

        var workflow = await _workflowDefinitionStore.GetAsync(workflowId, cancellationToken)
            ?? throw new NotFoundException("WorkflowDefinition", workflowId);

        var sourceConnectionId = ExtractWorkflowSourceConnectionId(workflow)
            ?? throw new InvalidOperationException("The workflow has no source node referencing a source connection.");

        return await GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
    }

    private static Guid? ExtractWorkflowSourceConnectionId(WorkflowDefinition workflow)
    {
        foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Source))
        {
            if (string.IsNullOrWhiteSpace(node.ConfigurationJson))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(node.ConfigurationJson);
                if (document.RootElement.TryGetProperty("sourceConnectionId", out var property)
                    && Guid.TryParse(property.GetString(), out var sourceConnectionId))
                {
                    return sourceConnectionId;
                }
            }
            catch (JsonException)
            {
                // Malformed node config — try the next source node.
            }
        }

        return null;
    }

    private async Task<SmartConfigurationDto> DiscoverEndpointsAsync(
        Guid sourceConnectionId, CancellationToken cancellationToken, string? overrideBaseUrl = null)
    {
        var smartConfiguration = await _discoveryService.DiscoverSmartConfigurationAsync(sourceConnectionId, overrideBaseUrl, cancellationToken);
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

    // Every anonymous OAuth entry point (EHR launch, standalone, patient) is reachable by any third party on the
    // internet — a stale/tampered context, an untrusted issuer, or a deleted workflow/route are all indistinguishable
    // from a probing attacker until resolved. Recording every failure here (not just successes) means the operational
    // audit log — the same table surfaced as the lineage/compliance trail elsewhere in the app — captures who tried
    // to reach the API and why it was refused, even when no source connection could be resolved.
    private Task RecordAccessAttemptFailureAsync(
        string entryPoint,
        string? issuer,
        Guid? sourceConnectionId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        _logger.LogWarning(
            exception,
            "Third-party OAuth access attempt via {EntryPoint} was rejected (iss {Issuer}, source {SourceConnectionId}).",
            entryPoint,
            issuer ?? "n/a",
            sourceConnectionId?.ToString() ?? "unresolved");

        var message = issuer is null
            ? exception.Message
            : $"{exception.Message} (iss {issuer})";

        return _auditService.RecordAsync(
            new RecordOperationalAuditLogRequest(
                null,
                null,
                sourceConnectionId,
                null,
                null,
                null,
                $"{entryPoint}Rejected",
                "Rejected",
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);
    }
}
