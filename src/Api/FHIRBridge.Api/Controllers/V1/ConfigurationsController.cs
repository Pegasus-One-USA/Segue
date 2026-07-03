using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// De-tenanted configuration CRUD. Successor to <c>TenantConfigurationsController</c> with the tenant route segment
/// removed. Source-connection testing, capability discovery, destination schema, pipeline runs, and user access live
/// in their own controllers.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1")]
public sealed class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configurationService;

    public ConfigurationsController(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    // ── Source connections ────────────────────────────────────────────────────

    [HttpPost("source-connections")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddSourceConnection(
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.AddSourceConnectionAsync(request, cancellationToken);

        return Created($"/api/v1/source-connections/{sourceConnection.Id}", sourceConnection);
    }

    [HttpPut("source-connections/{sourceConnectionId:guid}")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSourceConnection(
        Guid sourceConnectionId,
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.UpdateSourceConnectionAsync(
            sourceConnectionId,
            request,
            cancellationToken);

        return Ok(sourceConnection);
    }

    [HttpPost("source-connections/{sourceConnectionId:guid}/deactivate")]
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
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddWebhookConfiguration(
        [FromBody] CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _configurationService.AddWebhookConfigurationAsync(request, cancellationToken);

        return Created($"/api/v1/webhooks/{webhookConfiguration.Id}", webhookConfiguration);
    }

    [HttpPost("webhooks/{webhookConfigurationId:guid}/deactivate")]
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
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddDestinationConfiguration(
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _configurationService.AddDestinationConfigurationAsync(request, cancellationToken);

        return Created($"/api/v1/destinations/{destinationConfiguration.Id}", destinationConfiguration);
    }

    [HttpPut("destinations/{destinationId:guid}")]
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
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddMappingProfile(
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.AddMappingProfileAsync(request, cancellationToken);

        return Created($"/api/v1/mapping-profiles/{mappingProfile.Id}", mappingProfile);
    }

    [HttpPut("mapping-profiles/{mappingProfileId:guid}")]
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
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> ConfigureResource(
        [FromBody] ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _configurationService.ConfigureResourceAsync(request, cancellationToken);

        return Created($"/api/v1/resources/{resourceConfiguration.ResourceType}", resourceConfiguration);
    }

    [HttpPost("resources/{resourceType}/deactivate")]
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
