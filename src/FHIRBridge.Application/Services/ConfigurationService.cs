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

    public ConfigurationService(
        IConfigurationRepository repository,
        ISourceCapabilityRepository capabilityRepository,
        ISourceCapabilityDiscoveryService capabilityDiscoveryService,
        IOperationalAuditService auditService,
        ICurrentUserService currentUserService)
    {
        _repository = repository;
        _capabilityRepository = capabilityRepository;
        _capabilityDiscoveryService = capabilityDiscoveryService;
        _auditService = auditService;
        _currentUserService = currentUserService;
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
            ConfigurationMapper.ToDomain(request.Interactive));

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
            ConfigurationMapper.ToDomain(request.Interactive));

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
        var destinationConfiguration = new DestinationConfiguration(
            request.Name,
            request.DestinationType,
            new SecretReference(request.KeyVaultName, request.SecretName),
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
        destinationConfiguration.Update(
            request.Name,
            request.DestinationType,
            new SecretReference(request.KeyVaultName, request.SecretName),
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

    private static void ValidateHttpsUrl(string? value, string fieldName)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{fieldName} must be an absolute HTTPS URL.");
        }
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
