using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

/// <summary>
/// De-tenanted configuration CRUD. Successor to <c>UnifiedTenantConfigurationService</c>: talks directly to the flat
/// <see cref="IConfigurationRepository"/> instead of loading/saving a Tenant aggregate. Resource "groups" are derived
/// from routes by resolving each route's resource type through its mapping profile.
/// </summary>
public sealed class ConfigurationService : IConfigurationService
{
    private readonly IConfigurationRepository _repository;
    private readonly ISourceCapabilityRepository _capabilityRepository;
    private readonly ISourceCapabilityDiscoveryService _capabilityDiscoveryService;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;
    private readonly ISecretWriter _secretWriter;

    public ConfigurationService(
        IConfigurationRepository repository,
        ISourceCapabilityRepository capabilityRepository,
        ISourceCapabilityDiscoveryService capabilityDiscoveryService,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService,
        ISecretWriter secretWriter)
    {
        _repository = repository;
        _capabilityRepository = capabilityRepository;
        _capabilityDiscoveryService = capabilityDiscoveryService;
        _auditService = auditService;
        _currentUserService = currentUserService;
        _secretWriter = secretWriter;
    }

    public async Task<SourceConnectionDto> AddSourceConnectionAsync(
        CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSourceConnectionRequest(request);
        var sourceConnection = new SourceConnection(
            request.Name,
            request.SourceSystemType,
            request.BaseUrl,
            ConfigurationMapper.ToDomain(request.Authentication),
            request.ApplicationType,
            ConfigurationMapper.ToDomain(request.Interactive),
            ConfigurationMapper.ToDomain(request.Retrieval));

        await _repository.AddSourceConnectionAsync(sourceConnection, cancellationToken);
        await RecordConfigurationAuditAsync(
            sourceConnection.Id,
            "SourceConnectionConfigured",
            $"Source connection configured for {request.SourceSystemType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<SourceConnectionDto> UpdateSourceConnectionAsync(
        Guid sourceConnectionId,
        CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSourceConnectionRequest(request);
        var sourceConnection = await GetSourceConnectionRequiredAsync(sourceConnectionId, cancellationToken);
        sourceConnection.Update(
            request.Name,
            request.SourceSystemType,
            request.BaseUrl,
            ConfigurationMapper.ToDomain(request.Authentication),
            request.ApplicationType,
            ConfigurationMapper.ToDomain(request.Interactive),
            ConfigurationMapper.ToDomain(request.Retrieval));

        await _repository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);
        await RecordConfigurationAuditAsync(
            sourceConnection.Id,
            "SourceConnectionUpdated",
            $"Source connection updated for {request.SourceSystemType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<SourceConnectionDto> SetSourceConnectionEnabledAsync(
        Guid sourceConnectionId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await GetSourceConnectionRequiredAsync(sourceConnectionId, cancellationToken);
        sourceConnection.SetEnabled(isEnabled);

        await _repository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);
        await RecordConfigurationAuditAsync(
            sourceConnection.Id,
            isEnabled ? "SourceConnectionActivated" : "SourceConnectionDeactivated",
            $"Source connection {sourceConnection.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<WebhookConfigurationDto> AddWebhookConfigurationAsync(
        CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = new WebhookConfiguration(
            request.SourceConnectionId,
            request.ResourceType,
            request.Name,
            request.Path,
            request.IsEnabled);

        await _repository.AddWebhookAsync(webhookConfiguration, cancellationToken);
        await RecordConfigurationAuditAsync(
            webhookConfiguration.SourceConnectionId,
            "WebhookConfigured",
            $"Webhook configuration saved for {request.ResourceType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(webhookConfiguration);
    }

    public async Task<WebhookConfigurationDto> SetWebhookConfigurationEnabledAsync(
        Guid webhookConfigurationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await GetWebhookRequiredAsync(webhookConfigurationId, cancellationToken);
        webhookConfiguration.SetEnabled(isEnabled);

        await _repository.UpdateWebhookAsync(webhookConfiguration, cancellationToken);
        await RecordConfigurationAuditAsync(
            webhookConfiguration.SourceConnectionId,
            isEnabled ? "WebhookActivated" : "WebhookDeactivated",
            $"Webhook configuration {webhookConfiguration.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(webhookConfiguration);
    }

    public async Task<DestinationConfigurationDto> AddDestinationConfigurationAsync(
        CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var secretReference = new SecretReference(request.KeyVaultName, request.SecretName);
        if (!string.IsNullOrWhiteSpace(request.InlineSecret))
        {
            await _secretWriter.WriteSecretAsync(secretReference, request.InlineSecret!, cancellationToken);
        }

        var destinationConfiguration = new DestinationConfiguration(
            request.Name,
            request.DestinationType,
            secretReference,
            request.Target);

        await _repository.AddDestinationAsync(destinationConfiguration, cancellationToken);
        await RecordConfigurationAuditAsync(
            null,
            "DestinationConfigured",
            $"Destination configuration created for {request.DestinationType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<DestinationConfigurationDto> UpdateDestinationConfigurationAsync(
        Guid destinationId,
        CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await GetDestinationRequiredAsync(destinationId, cancellationToken);
        var secretReference = new SecretReference(request.KeyVaultName, request.SecretName);
        if (!string.IsNullOrWhiteSpace(request.InlineSecret))
        {
            await _secretWriter.WriteSecretAsync(secretReference, request.InlineSecret!, cancellationToken);
        }

        destinationConfiguration.Update(
            request.Name,
            request.DestinationType,
            secretReference,
            request.Target);

        await _repository.UpdateDestinationAsync(destinationConfiguration, cancellationToken);
        await RecordConfigurationAuditAsync(
            null,
            "DestinationUpdated",
            $"Destination configuration {destinationConfiguration.Name} updated for {request.DestinationType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<DestinationConfigurationDto> SetDestinationConfigurationEnabledAsync(
        Guid destinationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await GetDestinationRequiredAsync(destinationId, cancellationToken);
        destinationConfiguration.SetEnabled(isEnabled);

        await _repository.UpdateDestinationAsync(destinationConfiguration, cancellationToken);
        await RecordConfigurationAuditAsync(
            null,
            isEnabled ? "DestinationActivated" : "DestinationDeactivated",
            $"Destination configuration {destinationConfiguration.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<MappingProfileDto> AddMappingProfileAsync(
        CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSourceSupportsResourceTypeAsync(request.SourceConnectionId, request.ResourceType, cancellationToken);
        var mappingProfile = new MappingProfile(
            request.Name,
            request.ResourceType,
            request.SourceConnectionId,
            request.DestinationId,
            request.DestinationObject,
            request.Fields.Select(ConfigurationMapper.ToDomain));

        await _repository.AddMappingProfileAsync(mappingProfile, cancellationToken);
        await RecordConfigurationAuditAsync(
            null,
            "MappingProfileConfigured",
            $"Mapping profile configured for {mappingProfile.ResourceType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> UpdateMappingProfileAsync(
        Guid mappingProfileId,
        CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        await EnsureSourceSupportsResourceTypeAsync(request.SourceConnectionId, request.ResourceType, cancellationToken);
        var mappingProfile = await GetMappingProfileRequiredAsync(mappingProfileId, cancellationToken);
        mappingProfile.Update(
            request.Name,
            request.ResourceType,
            request.SourceConnectionId,
            request.DestinationId,
            request.DestinationObject,
            request.Fields.Select(ConfigurationMapper.ToDomain));

        await _repository.UpdateMappingProfileAsync(mappingProfile, cancellationToken);
        await RecordConfigurationAuditAsync(
            null,
            "MappingProfileUpdated",
            $"Mapping profile {mappingProfile.Name} updated for {mappingProfile.ResourceType}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> SetMappingProfileEnabledAsync(
        Guid mappingProfileId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await GetMappingProfileRequiredAsync(mappingProfileId, cancellationToken);
        mappingProfile.SetEnabled(isEnabled);

        await _repository.UpdateMappingProfileAsync(mappingProfile, cancellationToken);
        await RecordConfigurationAuditAsync(
            null,
            isEnabled ? "MappingProfileActivated" : "MappingProfileDeactivated",
            $"Mapping profile {mappingProfile.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<ResourceConfigurationDto> ConfigureResourceAsync(
        ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        // A route's resource type, source, and destination are all owned by the mapping profile.
        var route = new ResourcePipelineRoute(
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.IngestionMode,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            priority: 0);

        await _repository.AddRouteAsync(route, cancellationToken);

        var resourceType = await ResolveResourceTypeAsync(route, cancellationToken) ?? "Unknown";
        await RecordConfigurationAuditAsync(
            await ResolveSourceConnectionIdAsync(route, cancellationToken),
            "ResourceConfigured",
            $"Resource route saved for {resourceType}.",
            cancellationToken);

        return await BuildResourceGroupDtoAsync(resourceType, cancellationToken);
    }

    public async Task<ResourceConfigurationDto> SetResourceConfigurationEnabledAsync(
        string resourceType,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var routes = await GetRoutesForResourceTypeAsync(resourceType, cancellationToken);
        foreach (var route in routes)
        {
            route.SetEnabled(isEnabled);
            await _repository.UpdateRouteAsync(route, cancellationToken);
        }

        await RecordConfigurationAuditAsync(
            null,
            isEnabled ? "ResourceActivated" : "ResourceDeactivated",
            $"Routes for resource type {resourceType} were {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return await BuildResourceGroupDtoAsync(resourceType, cancellationToken);
    }

    public async Task<ResourcePipelineRouteDto> AddResourceRouteAsync(
        string resourceType,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        // resourceType path segment is ignored; the route's resource type, source, and destination come from its mapping.
        var route = new ResourcePipelineRoute(
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.IngestionMode,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            request.Priority);
        await ApplyResourceMappingsAsync(route, request, cancellationToken);

        await _repository.AddRouteAsync(route, cancellationToken);
        await RecordConfigurationAuditAsync(
            await ResolveSourceConnectionIdAsync(route, cancellationToken),
            "ResourceRouteConfigured",
            $"Resource route configured for {await ResolveResourceTypeAsync(route, cancellationToken) ?? "Unknown"}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(route);
    }

    public async Task<ResourcePipelineRouteDto> UpdateResourceRouteAsync(
        string resourceType,
        Guid routeId,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await GetRouteRequiredAsync(routeId, cancellationToken);
        route.Update(
            request.IngestionMode,
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            request.Priority);
        await ApplyResourceMappingsAsync(route, request, cancellationToken);

        await _repository.UpdateRouteAsync(route, cancellationToken);
        await RecordConfigurationAuditAsync(
            await ResolveSourceConnectionIdAsync(route, cancellationToken),
            "ResourceRouteUpdated",
            $"Resource route {route.Id} updated for {await ResolveResourceTypeAsync(route, cancellationToken) ?? "Unknown"}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(route);
    }

    public async Task<ResourcePipelineRouteDto> SetResourceRouteEnabledAsync(
        string resourceType,
        Guid routeId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var route = await GetRouteRequiredAsync(routeId, cancellationToken);
        route.SetEnabled(isEnabled);

        await _repository.UpdateRouteAsync(route, cancellationToken);
        await RecordConfigurationAuditAsync(
            await ResolveSourceConnectionIdAsync(route, cancellationToken),
            isEnabled ? "ResourceRouteActivated" : "ResourceRouteDeactivated",
            $"Resource route {route.Id} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return ConfigurationMapper.ToDto(route);
    }

    // ── Route resource-type resolution (via mapping profile) ───────────────────

    private async Task ApplyResourceMappingsAsync(
        ResourcePipelineRoute route,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ResourceMappings is not { Count: > 0 })
        {
            route.ReplaceResourceMappings([]);
            return;
        }

        var primaryMapping = await GetMappingProfileRequiredAsync(request.MappingProfileId, cancellationToken);
        var normalizedMappings = new List<ResourcePipelineRouteMapping>();
        var seen = new HashSet<Guid>();

        foreach (var mappingRequest in request.ResourceMappings)
        {
            if (!seen.Add(mappingRequest.MappingProfileId))
            {
                throw new InvalidOperationException(
                    $"Route resource mapping '{mappingRequest.MappingProfileId}' is duplicated.");
            }

            var mapping = await GetMappingProfileRequiredAsync(mappingRequest.MappingProfileId, cancellationToken);
            if (mapping.SourceConnectionId != primaryMapping.SourceConnectionId)
            {
                throw new InvalidOperationException(
                    "All resource mappings on a route must use mapping profiles from the same source connection.");
            }

            normalizedMappings.Add(new ResourcePipelineRouteMapping(
                mappingRequest.MappingProfileId,
                mappingRequest.IsEnabled,
                mappingRequest.ExecutionOrder,
                mappingRequest.SearchParameters));
        }

        if (!seen.Contains(request.MappingProfileId))
        {
            normalizedMappings.Add(new ResourcePipelineRouteMapping(
                request.MappingProfileId,
                isEnabled: true,
                executionOrder: 0));
        }

        route.ReplaceResourceMappings(normalizedMappings);
    }

    private async Task<string?> ResolveResourceTypeAsync(ResourcePipelineRoute route, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetMappingProfileAsync(route.MappingProfileId, cancellationToken);
        return mapping?.ResourceType;
    }

    private async Task<Guid?> ResolveSourceConnectionIdAsync(ResourcePipelineRoute route, CancellationToken cancellationToken)
    {
        var mapping = await _repository.GetMappingProfileAsync(route.MappingProfileId, cancellationToken);
        return mapping?.SourceConnectionId;
    }

    private async Task<IReadOnlyList<ResourcePipelineRoute>> GetRoutesForResourceTypeAsync(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var routes = await _repository.GetRoutesAsync(cancellationToken);
        var mappings = (await _repository.GetMappingProfilesAsync(cancellationToken))
            .ToDictionary(x => x.Id);

        return routes
            .Where(route =>
                mappings.TryGetValue(route.MappingProfileId, out var mapping) &&
                string.Equals(mapping.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task<ResourceConfigurationDto> BuildResourceGroupDtoAsync(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var routes = await GetRoutesForResourceTypeAsync(resourceType, cancellationToken);

        return new ResourceConfigurationDto(
            Guid.Empty,
            resourceType,
            routes.Any(route => route.IsEnabled),
            routes
                .OrderBy(route => route.Priority)
                .ThenBy(route => route.Id)
                .Select(ConfigurationMapper.ToDto)
                .ToList());
    }

    // ── Required getters ───────────────────────────────────────────────────────

    private async Task<SourceConnection> GetSourceConnectionRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetSourceConnectionAsync(id, cancellationToken)
        ?? throw new NotFoundException("SourceConnection", id);

    private async Task<WebhookConfiguration> GetWebhookRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetWebhookAsync(id, cancellationToken)
        ?? throw new NotFoundException("WebhookConfiguration", id);

    private async Task<DestinationConfiguration> GetDestinationRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetDestinationAsync(id, cancellationToken)
        ?? throw new NotFoundException("DestinationConfiguration", id);

    private async Task<MappingProfile> GetMappingProfileRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetMappingProfileAsync(id, cancellationToken)
        ?? throw new NotFoundException("MappingProfile", id);

    private async Task<ResourcePipelineRoute> GetRouteRequiredAsync(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetRouteAsync(id, cancellationToken)
        ?? throw new NotFoundException("ResourcePipelineRoute", id);

    /// <summary>
    /// Hard-blocks saving a mapping whose FHIR resource type the chosen source cannot provide, per its discovered
    /// capability profile. When no snapshot exists yet, discovery is run on demand for sources that support it (Epic);
    /// if the source type has no discovery support, we cannot prove the resource type is invalid and so fail open.
    /// </summary>
    private async Task EnsureSourceSupportsResourceTypeAsync(
        Guid sourceConnectionId,
        string resourceType,
        CancellationToken cancellationToken)
    {
        var capability = await _capabilityRepository.GetBySourceConnectionIdAsync(sourceConnectionId, cancellationToken);

        if (capability is null)
        {
            var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
            if (sourceConnection is null || sourceConnection.SourceSystemType != SourceSystemType.Epic)
            {
                // Discovery is not implemented for this source type, so we cannot prove the resource type is
                // unsupported — fail open rather than lock authors out.
                return;
            }

            // Capability discovery calls /metadata with a SMART Backend Services token (client_credentials +
            // private_key_jwt). Interactive sources (EHR launch / provider standalone / patient) authenticate as a
            // user via authorization_code and have no private key at configuration time, so discovery cannot run
            // until a user completes a launch. Fail open for them rather than block authoring the mapping.
            var isInteractive = sourceConnection.ApplicationType is ApplicationType.EhrLaunch
                or ApplicationType.Standalone
                or ApplicationType.Patient;
            if (isInteractive)
            {
                return;
            }

            await _capabilityDiscoveryService.DiscoverAsync(sourceConnectionId, cancellationToken);
            capability = await _capabilityRepository.GetBySourceConnectionIdAsync(sourceConnectionId, cancellationToken);

            if (capability is null)
            {
                return;
            }
        }

        if (!capability.SupportsResourceType(resourceType))
        {
            throw new InvalidOperationException(
                $"The selected source does not support FHIR resource type '{resourceType}'. " +
                $"Its capability statement (discovered {capability.DiscoveredOnUtc:u}) does not expose that type " +
                "with a read or search interaction. Refresh the source's capabilities or choose a different resource type.");
        }
    }

    private static void ValidateSourceConnectionRequest(CreateSourceConnectionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Source connection name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.BaseUrl))
        {
            throw new InvalidOperationException("Source FHIR base URL is required.");
        }

        if (request.SourceSystemType == SourceSystemType.Epic)
        {
            ValidateEpicSourceConnection(request);
        }

        // Retrieval config is a Backend System / Provider Standalone concept (vendor-agnostic — Epic, Cerner, or
        // any other source) independent of the Epic-specific checks above. Backend System gets the full method
        // picker for unattended, recurring execution; Provider Standalone gets a curated Search REST subset (no
        // Run Mode/scheduler — a one-shot, user-initiated fetch has no recurring run to schedule) — the DTO simply
        // arrives with those fields null for Standalone, which every check below already tolerates since they're
        // optional. EHR-launch / patient never populate it, so this is a no-op for them.
        if (request.ApplicationType is ApplicationType.Backend or ApplicationType.Standalone && request.Retrieval is not null)
        {
            ValidateRetrievalConfiguration(request.Retrieval);
        }
    }

    private static void ValidateRetrievalConfiguration(SourceRetrievalConfigurationDto retrieval)
    {
        if (string.IsNullOrWhiteSpace(retrieval.RetrievalMethod))
        {
            throw new InvalidOperationException("A data retrieval method is required for Backend System or Provider Standalone sources.");
        }

        if (retrieval.RetrievalMethod == "search-rest" && (retrieval.ResourceTypes is null || retrieval.ResourceTypes.Length == 0))
        {
            throw new InvalidOperationException("At least one resource type is required for Search (REST) retrieval.");
        }

        if (retrieval.PageSize is <= 0)
        {
            throw new InvalidOperationException("Page size must be a positive number.");
        }

        if (retrieval.TimeoutSeconds is <= 0)
        {
            throw new InvalidOperationException("Timeout must be a positive number of seconds.");
        }

        if (retrieval.MaxRecordsPerRun is <= 0)
        {
            throw new InvalidOperationException("Max records per run must be a positive number.");
        }
    }

    private static void ValidateEpicSourceConnection(CreateSourceConnectionRequest request)
    {
        ValidateHttpsUrl(request.BaseUrl, "Epic FHIR base URL");

        if (string.IsNullOrWhiteSpace(request.Authentication.ClientId))
        {
            throw new InvalidOperationException("Epic client id is required.");
        }

        if (request.Authentication.Scopes is null || request.Authentication.Scopes.Length == 0)
        {
            throw new InvalidOperationException("Epic scopes are required.");
        }

        // Interactive provider flows (EHR launch / provider standalone / patient) authenticate via
        // authorization_code + PKCE, selected by ApplicationType. Their authorize/token endpoints are discovered
        // from the source's .well-known/smart-configuration at runtime, so no token endpoint / KeyId / private key
        // is required at configuration time (the client may be public PKCE or confidential).
        var isInteractive = request.ApplicationType is ApplicationType.EhrLaunch
            or ApplicationType.Standalone
            or ApplicationType.Patient;

        if (isInteractive)
        {
            if (request.Interactive is null || request.Interactive.RedirectUris.Length == 0)
            {
                throw new InvalidOperationException("An interactive Epic source connection requires at least one redirect URI.");
            }

            if (request.ApplicationType == ApplicationType.EhrLaunch &&
                (request.Interactive.TrustedIssuers is null || request.Interactive.TrustedIssuers.Length == 0))
            {
                throw new InvalidOperationException("An Epic EHR-launch source connection requires at least one trusted issuer.");
            }

            return;
        }

        // A loopback base URL (e.g. docker-compose's local HAPI FHIR) is never a real Epic tenant — it has no OAuth
        // server to exchange a private_key_jwt assertion with, so the SMART Backend Services requirement below
        // would be unsatisfiable no matter what's configured. Skip it entirely for loopback; any real (non-loopback)
        // Epic endpoint still requires full JWT/Key Vault setup, unchanged.
        if (IsLoopbackUrl(request.BaseUrl))
        {
            return;
        }

        // Backend Services (machine-to-machine): client_credentials + private_key_jwt.
        if (request.Authentication.AuthenticationType != AuthenticationType.SmartBackendServices)
        {
            throw new InvalidOperationException("A Backend Services Epic source connection must use SMART Backend Services authentication.");
        }

        ValidateHttpsUrl(request.Authentication.TokenEndpoint, "Epic token endpoint");

        if (string.IsNullOrWhiteSpace(request.Authentication.KeyId))
        {
            throw new InvalidOperationException("Epic public key id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Authentication.PrivateKeyKeyVaultName) ||
            string.IsNullOrWhiteSpace(request.Authentication.PrivateKeySecretName))
        {
            throw new InvalidOperationException("Epic private key must be stored as a secret reference.");
        }
    }

    private static bool IsLoopbackUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.IsLoopback;

    private static void ValidateHttpsUrl(string? value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"{fieldName} must be an absolute HTTPS URL.");
        }

        if (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Mirrors SourceConnection.ValidateBaseUrl's loopback exception, so a local/dev FHIR server (e.g.
        // docker-compose's HAPI FHIR at http://localhost:8080/fhir) can be configured without relaxing the HTTPS
        // requirement for real endpoints.
        if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) && uri.IsLoopback)
        {
            return;
        }

        throw new InvalidOperationException(
            $"{fieldName} must be an absolute HTTPS URL (plain HTTP is allowed only for loopback addresses).");
    }

    private Task RecordConfigurationAuditAsync(
        Guid? sourceConnectionId,
        string action,
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
                "Completed",
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);
    }
}
