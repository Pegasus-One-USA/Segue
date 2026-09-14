using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// De-tenanted configuration CRUD service. Successor to <c>IUnifiedTenantConfigurationService</c> with all tenant
/// creation/lookup and every <c>tenantId</c> parameter removed — configuration is now single-org and flat.
/// </summary>
public interface IConfigurationService
{
    Task<SourceConnectionDto> AddSourceConnectionAsync(CreateSourceConnectionRequest request, CancellationToken cancellationToken);

    Task<SourceConnectionDto?> GetSourceConnectionByIdAsync(Guid sourceConnectionId, CancellationToken cancellationToken);

    Task<SourceConnectionDto> UpdateSourceConnectionAsync(Guid sourceConnectionId, CreateSourceConnectionRequest request, CancellationToken cancellationToken);

    Task<SourceConnectionDto> SetSourceConnectionEnabledAsync(Guid sourceConnectionId, bool isEnabled, CancellationToken cancellationToken);

    Task DeleteSourceConnectionAsync(Guid sourceConnectionId, CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceConfigurationDto>> GetSourceConfigurationsAsync(CancellationToken cancellationToken);

    Task<SourceConfigurationDto> AddSourceConfigurationAsync(CreateSourceConfigurationRequest request, CancellationToken cancellationToken);

    Task<SourceConfigurationDto?> GetSourceConfigurationByIdAsync(Guid sourceConfigurationId, CancellationToken cancellationToken);

    Task<SourceConfigurationDto> UpdateSourceConfigurationAsync(Guid sourceConfigurationId, CreateSourceConfigurationRequest request, CancellationToken cancellationToken);

    Task DeleteSourceConfigurationAsync(Guid sourceConfigurationId, CancellationToken cancellationToken);

    Task<WebhookConfigurationDto> AddWebhookConfigurationAsync(CreateWebhookConfigurationRequest request, CancellationToken cancellationToken);

    Task<WebhookConfigurationDto> SetWebhookConfigurationEnabledAsync(Guid webhookConfigurationId, bool isEnabled, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> AddDestinationConfigurationAsync(CreateDestinationConfigurationRequest request, CancellationToken cancellationToken);

    Task<PagedResult<DestinationConfigurationDto>> GetDestinationConfigurationsPagedAsync(
        DestinationFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> UpdateDestinationConfigurationAsync(Guid destinationId, CreateDestinationConfigurationRequest request, CancellationToken cancellationToken);

    Task<DestinationConfigurationDto> SetDestinationConfigurationEnabledAsync(Guid destinationId, bool isEnabled, CancellationToken cancellationToken);

    /// <summary>
    /// Sets (or clears, when <paramref name="deIdentificationProfileId"/> is null) the de-identification profile a
    /// destination's resources are redacted against. Deliberately narrow rather than routed through
    /// <see cref="UpdateDestinationConfigurationAsync"/>: that path runs the full create-request validator, which
    /// requires KeyVaultName/SecretName on every call — values the destination wizard never re-displays for a
    /// stored secret, so a profile-only change would have to fabricate them to pass validation.
    /// </summary>
    Task<DestinationConfigurationDto> SetDestinationConfigurationDeIdentificationProfileAsync(
        Guid destinationId, Guid? deIdentificationProfileId, CancellationToken cancellationToken);

    Task DeleteDestinationConfigurationAsync(Guid destinationId, CancellationToken cancellationToken);

    Task<bool> HasDestinationExecutionHistoryAsync(Guid destinationId, CancellationToken cancellationToken);

    Task<MappingProfileDto> AddMappingProfileAsync(CreateMappingProfileRequest request, CancellationToken cancellationToken);

    Task<PagedResult<MappingProfileDto>> GetMappingProfilesPagedAsync(
        MappingProfileFilter filter, int page, int pageSize, string? sortBy, string? sortOrder, CancellationToken cancellationToken);

    Task<MappingProfileDto?> GetMappingProfileByIdAsync(Guid mappingProfileId, CancellationToken cancellationToken);

    Task<MappingProfileDto> UpdateMappingProfileAsync(Guid mappingProfileId, CreateMappingProfileRequest request, CancellationToken cancellationToken);

    /// <summary>Looks up a MappingProfile by its natural key (resourceType, sourceConnectionId, destinationId) —
    /// the same key <c>MappingImportService</c> de-duplicates on. Used by the workflow-build endpoint to detect
    /// (and reuse) a profile that already exists for this combination instead of creating/overwriting one.</summary>
    Task<MappingProfileDto?> FindMappingProfileAsync(
        string resourceType, Guid sourceConnectionId, Guid destinationId, CancellationToken cancellationToken);

    Task<MappingProfileDto> SetMappingProfileEnabledAsync(Guid mappingProfileId, bool isEnabled, CancellationToken cancellationToken);

    /// <summary>
    /// Promotes a workflow's own mapping profile into a new, independently-named master template other
    /// workflows can find and clone via "Select Existing" — always inserts a brand-new
    /// <see cref="Domain.Entities.MappingProfile"/> row (copying <paramref name="sourceMappingProfileId"/>'s
    /// resource type/source connection/destination/destination object/fields under the new name), never
    /// finds-and-overwrites an existing profile. Promoting is a one-time snapshot: the new master and the
    /// workflow's own profile are independent from this point on — editing one never affects the other.
    /// </summary>
    Task<MappingProfileDto> PromoteMappingProfileToMasterAsync(
        Guid sourceMappingProfileId, string masterName, CancellationToken cancellationToken);

    /// <summary>
    /// Number of <see cref="Domain.Entities.ResourcePipelineRoute"/> records that reference this mapping profile —
    /// as their primary mapping, as one of their composite <c>ResourceMappings</c>, or as a parent reference target.
    /// A non-zero count means <see cref="DeleteMappingProfileAsync"/> would fail against the Restrict FK; callers
    /// should surface this as a conflict before attempting the delete.
    /// </summary>
    Task<int> GetMappingProfileUsageCountAsync(Guid mappingProfileId, CancellationToken cancellationToken);

    Task DeleteMappingProfileAsync(Guid mappingProfileId, CancellationToken cancellationToken);

    Task<ResourceConfigurationDto> ConfigureResourceAsync(ConfigureResourceRequest request, CancellationToken cancellationToken);

    Task<ResourceConfigurationDto> SetResourceConfigurationEnabledAsync(string resourceType, bool isEnabled, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> AddResourceRouteAsync(string resourceType, CreateResourceRouteRequest request, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> UpdateResourceRouteAsync(string resourceType, Guid routeId, CreateResourceRouteRequest request, CancellationToken cancellationToken);

    Task<ResourcePipelineRouteDto> SetResourceRouteEnabledAsync(string resourceType, Guid routeId, bool isEnabled, CancellationToken cancellationToken);
}
