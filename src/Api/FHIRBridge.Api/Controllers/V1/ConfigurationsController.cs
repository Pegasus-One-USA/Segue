using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// De-tenanted configuration CRUD. Successor to <c>TenantConfigurationsController</c> with the tenant route segment
/// removed. Source-connection testing, capability discovery, destination schema, pipeline runs, and user access live
/// in their own controllers.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configurationService;
    private readonly IAuthorizationService _authorizationService;

    public ConfigurationsController(
        IConfigurationService configurationService,
        IAuthorizationService authorizationService)
    {
        _configurationService = configurationService;
        _authorizationService = authorizationService;
    }

    // ── Source connections ────────────────────────────────────────────────────
    // Which permission a source connection requires depends on its vendor (SourceSystemType), known
    // only once the request is inspected — a static [StandardPermission] can't express that, so these
    // two check a resolved, vendor-specific permission at runtime instead of declaring one up front.

    [HttpPost("source-connections")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Edit, description: "Add or edit a source connection.")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddSourceConnection(
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await this.AuthorizePermissionAsync(_authorizationService, request.SourceSystemType, PermissionActionCode.Edit);
        if (denied is not null) return denied;

        var sourceConnection = await _configurationService.AddSourceConnectionAsync(request, cancellationToken);

        return Created($"/api/v1/source-connections/{sourceConnection.Id}", sourceConnection);
    }

    [HttpPut("source-connections/{sourceConnectionId:guid}")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Edit, description: "Add or edit a source connection.")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSourceConnection(
        Guid sourceConnectionId,
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await this.AuthorizePermissionAsync(_authorizationService, request.SourceSystemType, PermissionActionCode.Edit);
        if (denied is not null) return denied;

        var sourceConnection = await _configurationService.UpdateSourceConnectionAsync(
            sourceConnectionId,
            request,
            cancellationToken);

        return Ok(sourceConnection);
    }

    // Deactivating only takes an id, not the connection's vendor — resolving that would need a lookup
    // first (the write-only IConfigurationService doesn't expose one yet), so this stays admin-only for
    // now rather than guess. Add a GetSourceConnectionByIdAsync + the same AuthorizePermissionAsync call
    // once that's needed.
    [HttpPost("source-connections/{sourceConnectionId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateSourceConnection(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.SetSourceConnectionEnabledAsync(
            sourceConnectionId,
            false,
            cancellationToken);

        return Ok(sourceConnection);
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    [HttpPost("webhooks")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddWebhookConfiguration(
        [FromBody] CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _configurationService.AddWebhookConfigurationAsync(request, cancellationToken);

        return Created($"/api/v1/webhooks/{webhookConfiguration.Id}", webhookConfiguration);
    }

    [HttpPost("webhooks/{webhookConfigurationId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateWebhookConfiguration(
        Guid webhookConfigurationId,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _configurationService.SetWebhookConfigurationEnabledAsync(
            webhookConfigurationId,
            false,
            cancellationToken);

        return Ok(webhookConfiguration);
    }

    // ── Destinations ──────────────────────────────────────────────────────────

    [HttpPost("destinations")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddDestinationConfiguration(
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _configurationService.AddDestinationConfigurationAsync(request, cancellationToken);

        return Created($"/api/v1/destinations/{destinationConfiguration.Id}", destinationConfiguration);
    }

    [HttpPut("destinations/{destinationId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateDestinationConfiguration(
        Guid destinationId,
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _configurationService.UpdateDestinationConfigurationAsync(
            destinationId,
            request,
            cancellationToken);

        return Ok(destinationConfiguration);
    }

    [HttpPost("destinations/{destinationId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateDestinationConfiguration(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _configurationService.SetDestinationConfigurationEnabledAsync(
            destinationId,
            false,
            cancellationToken);

        return Ok(destinationConfiguration);
    }

    // ── Mapping profiles ──────────────────────────────────────────────────────

    [HttpPost("mapping-profiles")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddMappingProfile(
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.AddMappingProfileAsync(request, cancellationToken);

        return Created($"/api/v1/mapping-profiles/{mappingProfile.Id}", mappingProfile);
    }

    [HttpPut("mapping-profiles/{mappingProfileId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMappingProfile(
        Guid mappingProfileId,
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.UpdateMappingProfileAsync(
            mappingProfileId,
            request,
            cancellationToken);

        return Ok(mappingProfile);
    }

    [HttpPost("mapping-profiles/{mappingProfileId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateMappingProfile(
        Guid mappingProfileId,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.SetMappingProfileEnabledAsync(
            mappingProfileId,
            false,
            cancellationToken);

        return Ok(mappingProfile);
    }

    // ── Resources / routes ────────────────────────────────────────────────────

    [HttpPost("resources")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> ConfigureResource(
        [FromBody] ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _configurationService.ConfigureResourceAsync(request, cancellationToken);

        return Created($"/api/v1/resources/{resourceConfiguration.ResourceType}", resourceConfiguration);
    }

    [HttpPost("resources/{resourceType}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateResourceConfiguration(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _configurationService.SetResourceConfigurationEnabledAsync(
            resourceType,
            false,
            cancellationToken);

        return Ok(resourceConfiguration);
    }

    [HttpPost("resources/{resourceType}/routes")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddResourceRoute(
        string resourceType,
        [FromBody] CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await _configurationService.AddResourceRouteAsync(resourceType, request, cancellationToken);

        return Created($"/api/v1/resources/{resourceType}/routes/{route.Id}", route);
    }

    [HttpPut("resources/{resourceType}/routes/{routeId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateResourceRoute(
        string resourceType,
        Guid routeId,
        [FromBody] CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await _configurationService.UpdateResourceRouteAsync(resourceType, routeId, request, cancellationToken);

        return Ok(route);
    }

    [HttpPost("resources/{resourceType}/routes/{routeId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateResourceRoute(
        string resourceType,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        var route = await _configurationService.SetResourceRouteEnabledAsync(resourceType, routeId, false, cancellationToken);

        return Ok(route);
    }
}
