using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IUnifiedTenantConfigurationService
{
    Task<TenantConfigurationDto> CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken);

    Task<TenantConfigurationDto?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<TenantConfigurationDto>> GetTenantsAsync(CancellationToken cancellationToken);

    Task<SourceConnectionDto> AddSourceConnectionAsync(Guid tenantId, CreateSourceConnectionRequest request, CancellationToken cancellationToken);

    Task<SourceConnectionDto> UpdateSourceConnectionAsync(Guid tenantId, Guid sourceConnectionId, CreateSourceConnectionRequest request, CancellationToken cancellationToken);

    Task<SourceConnectionDto> SetSourceConnectionEnabledAsync(Guid tenantId, Guid sourceConnectionId, bool isEnabled, CancellationToken cancellationToken);

    Task<WebhookConfigurationDto> AddWebhookConfigurationAsync(Guid tenantId, CreateWebhookConfigurationRequest request, CancellationToken cancellationToken);

    Task<WebhookConfigurationDto> SetWebhookConfigurationEnabledAsync(Guid tenantId, Guid webhookConfigurationId, bool isEnabled, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> AddDestinationConfigurationAsync(Guid tenantId, CreateDestinationConfigurationRequest request, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> UpdateDestinationConfigurationAsync(Guid tenantId, Guid destinationId, CreateDestinationConfigurationRequest request, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> SetDestinationConfigurationEnabledAsync(Guid tenantId, Guid destinationId, bool isEnabled, CancellationToken cancellationToken);

    Task<MappingProfileDto> AddMappingProfileAsync(Guid tenantId, CreateMappingProfileRequest request, CancellationToken cancellationToken);

    Task<MappingProfileDto> UpdateMappingProfileAsync(Guid tenantId, Guid mappingProfileId, CreateMappingProfileRequest request, CancellationToken cancellationToken);

    Task<MappingProfileDto> SetMappingProfileEnabledAsync(Guid tenantId, Guid mappingProfileId, bool isEnabled, CancellationToken cancellationToken);

    Task<ResourceConfigurationDto> ConfigureResourceAsync(Guid tenantId, ConfigureResourceRequest request, CancellationToken cancellationToken);

    Task<ResourceConfigurationDto> SetResourceConfigurationEnabledAsync(Guid tenantId, string resourceType, bool isEnabled, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> AddResourceRouteAsync(
        Guid tenantId,
        string resourceType,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> UpdateResourceRouteAsync(
        Guid tenantId,
        string resourceType,
        Guid routeId,
        CreateResourceRouteRequest request,
        CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> SetResourceRouteEnabledAsync(
        Guid tenantId,
        string resourceType,
        Guid routeId,
        bool isEnabled,
        CancellationToken cancellationToken);
}
