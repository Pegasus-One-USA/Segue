using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// De-tenanted configuration CRUD service. Successor to <c>IUnifiedTenantConfigurationService</c> with all tenant
/// creation/lookup and every <c>tenantId</c> parameter removed — configuration is now single-org and flat.
/// </summary>
public interface IConfigurationService
{
    Task<SourceConnectionDto> AddSourceConnectionAsync(CreateSourceConnectionRequest request, CancellationToken cancellationToken);

    Task<SourceConnectionDto> UpdateSourceConnectionAsync(Guid sourceConnectionId, CreateSourceConnectionRequest request, CancellationToken cancellationToken);

    Task<SourceConnectionDto> SetSourceConnectionEnabledAsync(Guid sourceConnectionId, bool isEnabled, CancellationToken cancellationToken);

    Task<WebhookConfigurationDto> AddWebhookConfigurationAsync(CreateWebhookConfigurationRequest request, CancellationToken cancellationToken);

    Task<WebhookConfigurationDto> SetWebhookConfigurationEnabledAsync(Guid webhookConfigurationId, bool isEnabled, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> AddDestinationConfigurationAsync(CreateDestinationConfigurationRequest request, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> UpdateDestinationConfigurationAsync(Guid destinationId, CreateDestinationConfigurationRequest request, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> SetDestinationConfigurationEnabledAsync(Guid destinationId, bool isEnabled, CancellationToken cancellationToken);

    Task<MappingProfileDto> AddMappingProfileAsync(CreateMappingProfileRequest request, CancellationToken cancellationToken);

    Task<MappingProfileDto> UpdateMappingProfileAsync(Guid mappingProfileId, CreateMappingProfileRequest request, CancellationToken cancellationToken);

    Task<MappingProfileDto> SetMappingProfileEnabledAsync(Guid mappingProfileId, bool isEnabled, CancellationToken cancellationToken);

    Task<ResourceConfigurationDto> ConfigureResourceAsync(ConfigureResourceRequest request, CancellationToken cancellationToken);

    Task<ResourceConfigurationDto> SetResourceConfigurationEnabledAsync(string resourceType, bool isEnabled, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> AddResourceRouteAsync(string resourceType, CreateResourceRouteRequest request, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> UpdateResourceRouteAsync(string resourceType, Guid routeId, CreateResourceRouteRequest request, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> SetResourceRouteEnabledAsync(string resourceType, Guid routeId, bool isEnabled, CancellationToken cancellationToken);
}
