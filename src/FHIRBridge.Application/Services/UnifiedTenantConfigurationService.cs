using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

public sealed class UnifiedTenantConfigurationService : IUnifiedTenantConfigurationService
{
    private readonly ITenantConfigurationRepository _repository;
    private readonly ISourceCapabilityRepository _capabilityRepository;
    private readonly ISourceCapabilityDiscoveryService _capabilityDiscoveryService;
    private readonly IOperationalAuditService _auditService;
    private readonly ICurrentUserService _currentUserService;

    public UnifiedTenantConfigurationService(
        ITenantConfigurationRepository repository,
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

    public async Task<TenantConfigurationDto> CreateTenantAsync(
        CreateTenantRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = new Tenant(request.Name, request.Code);

        await _repository.AddAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenant.Id,
            null,
            "TenantCreated",
            "Tenant configuration created.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(tenant);
    }

    public async Task<TenantConfigurationDto?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _repository.GetByIdAsync(tenantId, cancellationToken);

        return tenant is null ? null : TenantConfigurationMapper.ToDto(tenant);
    }

    public async Task<IReadOnlyList<TenantConfigurationDto>> GetTenantsAsync(CancellationToken cancellationToken)
    {
        var tenants = await _repository.GetAllAsync(cancellationToken);

        return tenants
            .Select(TenantConfigurationMapper.ToDto)
            .ToList();
    }

    public async Task<SourceConnectionDto> AddSourceConnectionAsync(
        Guid tenantId,
        CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSourceConnectionRequest(request);
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var sourceConnection = tenant.AddSourceConnection(
            request.Name,
            request.SourceSystemType,
            request.BaseUrl,
            TenantConfigurationMapper.ToDomain(request.Authentication));

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            sourceConnection.Id,
            "SourceConnectionConfigured",
            $"Source connection configured for {request.SourceSystemType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<SourceConnectionDto> UpdateSourceConnectionAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        ValidateSourceConnectionRequest(request);
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var sourceConnection = tenant.UpdateSourceConnection(
            sourceConnectionId,
            request.Name,
            request.SourceSystemType,
            request.BaseUrl,
            TenantConfigurationMapper.ToDomain(request.Authentication));

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            sourceConnection.Id,
            "SourceConnectionUpdated",
            $"Source connection updated for {request.SourceSystemType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<SourceConnectionDto> SetSourceConnectionEnabledAsync(
        Guid tenantId,
        Guid sourceConnectionId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var sourceConnection = tenant.SetSourceConnectionEnabled(sourceConnectionId, isEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            sourceConnection.Id,
            isEnabled ? "SourceConnectionActivated" : "SourceConnectionDeactivated",
            $"Source connection {sourceConnection.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(sourceConnection);
    }

    public async Task<WebhookConfigurationDto> AddWebhookConfigurationAsync(
        Guid tenantId,
        CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var webhookConfiguration = tenant.AddWebhookConfiguration(
            request.SourceConnectionId,
            request.ResourceType,
            request.Name,
            request.Path,
            request.IsEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            webhookConfiguration.SourceConnectionId,
            "WebhookConfigured",
            $"Webhook configuration saved for {request.ResourceType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(webhookConfiguration);
    }

    public async Task<WebhookConfigurationDto> SetWebhookConfigurationEnabledAsync(
        Guid tenantId,
        Guid webhookConfigurationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var webhookConfiguration = tenant.SetWebhookConfigurationEnabled(webhookConfigurationId, isEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            webhookConfiguration.SourceConnectionId,
            isEnabled ? "WebhookActivated" : "WebhookDeactivated",
            $"Webhook configuration {webhookConfiguration.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(webhookConfiguration);
    }

    public async Task<DestinationConfigurationDto> AddDestinationConfigurationAsync(
        Guid tenantId,
        CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var destinationConfiguration = tenant.AddDestinationConfiguration(
            request.Name,
            request.DestinationType,
            new SecretReference(request.KeyVaultName, request.SecretName),
            request.Target);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            "DestinationConfigured",
            $"Destination configuration created for {request.DestinationType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<DestinationConfigurationDto> UpdateDestinationConfigurationAsync(
        Guid tenantId,
        Guid destinationId,
        CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var destinationConfiguration = tenant.UpdateDestinationConfiguration(
            destinationId,
            request.Name,
            request.DestinationType,
            new SecretReference(request.KeyVaultName, request.SecretName),
            request.Target);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            "DestinationUpdated",
            $"Destination configuration {destinationConfiguration.Name} updated for {request.DestinationType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<DestinationConfigurationDto> SetDestinationConfigurationEnabledAsync(
        Guid tenantId,
        Guid destinationId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var destinationConfiguration = tenant.SetDestinationConfigurationEnabled(destinationId, isEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            isEnabled ? "DestinationActivated" : "DestinationDeactivated",
            $"Destination configuration {destinationConfiguration.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(destinationConfiguration);
    }

    public async Task<MappingProfileDto> AddMappingProfileAsync(
        Guid tenantId,
        CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        await EnsureSourceSupportsResourceTypeAsync(
            tenant, request.SourceConnectionId, request.ResourceType, cancellationToken);
        var mappingProfile = tenant.AddMappingProfile(
            request.Name,
            request.ResourceType,
            request.SourceConnectionId,
            request.DestinationId,
            request.DestinationObject,
            request.Fields.Select(TenantConfigurationMapper.ToDomain));

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            "MappingProfileConfigured",
            $"Mapping profile configured for {mappingProfile.ResourceType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> UpdateMappingProfileAsync(
        Guid tenantId,
        Guid mappingProfileId,
        CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        await EnsureSourceSupportsResourceTypeAsync(
            tenant, request.SourceConnectionId, request.ResourceType, cancellationToken);
        var mappingProfile = tenant.UpdateMappingProfile(
            mappingProfileId,
            request.Name,
            request.ResourceType,
            request.SourceConnectionId,
            request.DestinationId,
            request.DestinationObject,
            request.Fields.Select(TenantConfigurationMapper.ToDomain));

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            "MappingProfileUpdated",
            $"Mapping profile {mappingProfile.Name} updated for {mappingProfile.ResourceType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<MappingProfileDto> SetMappingProfileEnabledAsync(
        Guid tenantId,
        Guid mappingProfileId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var mappingProfile = tenant.SetMappingProfileEnabled(mappingProfileId, isEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            isEnabled ? "MappingProfileActivated" : "MappingProfileDeactivated",
            $"Mapping profile {mappingProfile.Name} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(mappingProfile);
    }

    public async Task<ResourceConfigurationDto> ConfigureResourceAsync(
        Guid tenantId,
        ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        // A route's resource type, source, and destination are all owned by the mapping profile.
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var route = tenant.AddRoute(
            request.IngestionMode,
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            priority: 0);

        await _repository.UpdateAsync(tenant, cancellationToken);
        var resourceType = tenant.ResolveResourceType(route) ?? "Unknown";
        await RecordConfigurationAuditAsync(
            tenantId,
            tenant.ResolveSourceConnectionId(route),
            "ResourceConfigured",
            $"Resource route saved for {resourceType}.",
            cancellationToken);

        return TenantConfigurationMapper.ToResourceGroupDto(tenant, resourceType);
    }

    public async Task<ResourceConfigurationDto> SetResourceConfigurationEnabledAsync(
        Guid tenantId,
        string resourceType,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        tenant.SetRoutesEnabledForResourceType(resourceType, isEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            null,
            isEnabled ? "ResourceActivated" : "ResourceDeactivated",
            $"Routes for resource type {resourceType} were {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return TenantConfigurationMapper.ToResourceGroupDto(tenant, resourceType);
    }

    public async Task<ResourcePipelineRouteDto> AddResourceRouteAsync(
        Guid tenantId,
        string resourceType,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        // resourceType path segment is ignored; the route's resource type, source, and destination come from its mapping.
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var route = tenant.AddRoute(
            request.IngestionMode,
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            request.Priority);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            tenant.ResolveSourceConnectionId(route),
            "ResourceRouteConfigured",
            $"Resource route configured for {tenant.ResolveResourceType(route) ?? "Unknown"}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(route);
    }

    public async Task<ResourcePipelineRouteDto> UpdateResourceRouteAsync(
        Guid tenantId,
        string resourceType,
        Guid routeId,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var route = tenant.UpdateRoute(
            routeId,
            request.IngestionMode,
            request.WebhookConfigurationId,
            request.MappingProfileId,
            request.ScheduleExpression,
            request.SearchParameters,
            request.IsEnabled,
            request.Priority);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            tenant.ResolveSourceConnectionId(route),
            "ResourceRouteUpdated",
            $"Resource route {route.Id} updated for {tenant.ResolveResourceType(route) ?? "Unknown"}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(route);
    }

    public async Task<ResourcePipelineRouteDto> SetResourceRouteEnabledAsync(
        Guid tenantId,
        string resourceType,
        Guid routeId,
        bool isEnabled,
        CancellationToken cancellationToken)
    {
        var tenant = await GetTenantRequiredAsync(tenantId, cancellationToken);
        var route = tenant.SetRouteEnabled(routeId, isEnabled);

        await _repository.UpdateAsync(tenant, cancellationToken);
        await RecordConfigurationAuditAsync(
            tenantId,
            tenant.ResolveSourceConnectionId(route),
            isEnabled ? "ResourceRouteActivated" : "ResourceRouteDeactivated",
            $"Resource route {route.Id} was {(isEnabled ? "activated" : "deactivated")}.",
            cancellationToken);

        return TenantConfigurationMapper.ToDto(route);
    }

    private async Task<Tenant> GetTenantRequiredAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _repository.GetByIdAsync(tenantId, cancellationToken);

        return tenant ?? throw new NotFoundException("Tenant", tenantId);
    }

    /// <summary>
    /// Hard-blocks saving a mapping whose FHIR resource type the chosen source cannot provide, per its discovered
    /// capability profile. A mapping owns its source connection, so this is the natural enforcement point — the
    /// resource type is validated against the same source every route built on this mapping will inherit.
    /// When no capability snapshot exists yet, discovery is run on demand for sources that support it (Epic), so the
    /// save is always validated against the source's real capabilities rather than silently allowed through. If the
    /// source type has no discovery support, we cannot prove the resource type is invalid and so fail open.
    /// </summary>
    private async Task EnsureSourceSupportsResourceTypeAsync(
        Tenant tenant,
        Guid sourceConnectionId,
        string resourceType,
        CancellationToken cancellationToken)
    {
        var capability = await _capabilityRepository.GetBySourceConnectionIdAsync(
            tenant.Id,
            sourceConnectionId,
            cancellationToken);

        if (capability is null)
        {
            var sourceConnection = tenant.SourceConnections.FirstOrDefault(x => x.Id == sourceConnectionId);
            if (sourceConnection is null || sourceConnection.SourceSystemType != SourceSystemType.Epic)
            {
                // Discovery is not implemented for this source type, so we cannot prove the resource type is
                // unsupported — fail open rather than lock authors out.
                return;
            }

            // No snapshot yet: discover the source's capabilities now so the save is validated against what the
            // source actually exposes. A discovery failure (e.g. source unreachable) propagates as the save error.
            await _capabilityDiscoveryService.DiscoverAsync(tenant.Id, sourceConnectionId, cancellationToken);
            capability = await _capabilityRepository.GetBySourceConnectionIdAsync(
                tenant.Id,
                sourceConnectionId,
                cancellationToken);

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
        if (request.Authentication.AuthenticationType != AuthenticationType.SmartBackendServices)
        {
            throw new InvalidOperationException("Epic Phase 1 source connections must use SMART Backend Services authentication.");
        }

        ValidateHttpsUrl(request.BaseUrl, "Epic FHIR base URL");
        ValidateHttpsUrl(request.Authentication.TokenEndpoint, "Epic token endpoint");

        if (string.IsNullOrWhiteSpace(request.Authentication.ClientId))
        {
            throw new InvalidOperationException("Epic client id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Authentication.KeyId))
        {
            throw new InvalidOperationException("Epic public key id is required.");
        }

        if (request.Authentication.Scopes is null || request.Authentication.Scopes.Length == 0)
        {
            throw new InvalidOperationException("Epic SMART Backend Services scopes are required.");
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
        Guid tenantId,
        Guid? sourceConnectionId,
        string action,
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
                "Completed",
                message,
                null,
                _currentUserService.CurrentUser.AuditName,
                null),
            cancellationToken);
    }
}
