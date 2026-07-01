using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/tenants")]
public sealed class TenantConfigurationsController : ControllerBase
{
    private readonly IUnifiedTenantConfigurationService _tenantConfigurationService;
    private readonly IUserAccessService _userAccessService;
    private readonly ISourceConnectionTestService _sourceConnectionTestService;
    private readonly IDestinationSchemaService _destinationSchemaService;
    private readonly IYamlManifestImportService _manifestImportService;

    public TenantConfigurationsController(
        IUnifiedTenantConfigurationService tenantConfigurationService,
        IUserAccessService userAccessService,
        ISourceConnectionTestService sourceConnectionTestService,
        IDestinationSchemaService destinationSchemaService,
        IYamlManifestImportService manifestImportService)
    {
        _tenantConfigurationService = tenantConfigurationService;
        _userAccessService = userAccessService;
        _sourceConnectionTestService = sourceConnectionTestService;
        _destinationSchemaService = destinationSchemaService;
        _manifestImportService = manifestImportService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(TenantConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateTenantRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = await _tenantConfigurationService.CreateTenantAsync(request, cancellationToken);

        return CreatedAtAction(nameof(GetById), new { tenantId = tenant.Id }, tenant);
    }

    [HttpPost("import")]
    [ProducesResponseType(typeof(ManifestImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportManifest(
        IFormFile file,
        CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { title = "Upload a non-empty YAML manifest file." });
        }

        using var reader = new StreamReader(file.OpenReadStream());
        var yaml = await reader.ReadToEndAsync(cancellationToken);

        var result = await _manifestImportService.ImportAsync(yaml, cancellationToken);

        return Ok(result);
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<TenantConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var tenants = await _tenantConfigurationService.GetTenantsAsync(cancellationToken);

        return Ok(tenants);
    }

    [HttpGet("{tenantId:guid}")]
    [ProducesResponseType(typeof(TenantConfigurationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await _tenantConfigurationService.GetTenantAsync(tenantId, cancellationToken);

        return tenant is null ? NotFound() : Ok(tenant);
    }

    [HttpPost("{tenantId:guid}/source-connections")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddSourceConnection(
        Guid tenantId,
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _tenantConfigurationService.AddSourceConnectionAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/source-connections/{sourceConnection.Id}", sourceConnection);
    }

    [HttpPut("{tenantId:guid}/source-connections/{sourceConnectionId:guid}")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSourceConnection(
        Guid tenantId,
        Guid sourceConnectionId,
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _tenantConfigurationService.UpdateSourceConnectionAsync(
            tenantId,
            sourceConnectionId,
            request,
            cancellationToken);

        return Ok(sourceConnection);
    }

    [HttpPost("{tenantId:guid}/source-connections/{sourceConnectionId:guid}/deactivate")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateSourceConnection(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _tenantConfigurationService.SetSourceConnectionEnabledAsync(
            tenantId,
            sourceConnectionId,
            false,
            cancellationToken);

        return Ok(sourceConnection);
    }

    [HttpPost("{tenantId:guid}/source-connections/{sourceConnectionId:guid}/test")]
    [ProducesResponseType(typeof(SourceConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSourceConnection(
        Guid tenantId,
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var result = await _sourceConnectionTestService.TestAsync(
            tenantId,
            sourceConnectionId,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("{tenantId:guid}/users")]
    [ProducesResponseType(typeof(IReadOnlyList<TenantUserDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetTenantUsers(
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var users = await _userAccessService.GetTenantUsersAsync(tenantId, cancellationToken);

        return Ok(users);
    }

    [HttpPost("{tenantId:guid}/users")]
    [ProducesResponseType(typeof(TenantUserDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AssignTenantUser(
        Guid tenantId,
        [FromBody] AssignTenantUserRequest request,
        CancellationToken cancellationToken)
    {
        var tenantUser = await _userAccessService.AssignTenantUserAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/users/{tenantUser.Id}", tenantUser);
    }

    [HttpPost("{tenantId:guid}/webhooks")]
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddWebhookConfiguration(
        Guid tenantId,
        [FromBody] CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _tenantConfigurationService.AddWebhookConfigurationAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/webhooks/{webhookConfiguration.Id}", webhookConfiguration);
    }

    [HttpPost("{tenantId:guid}/webhooks/{webhookConfigurationId:guid}/deactivate")]
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateWebhookConfiguration(
        Guid tenantId,
        Guid webhookConfigurationId,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _tenantConfigurationService.SetWebhookConfigurationEnabledAsync(
            tenantId,
            webhookConfigurationId,
            false,
            cancellationToken);

        return Ok(webhookConfiguration);
    }

    [HttpPost("{tenantId:guid}/destinations")]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddDestinationConfiguration(
        Guid tenantId,
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _tenantConfigurationService.AddDestinationConfigurationAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/destinations/{destinationConfiguration.Id}", destinationConfiguration);
    }

    [HttpPut("{tenantId:guid}/destinations/{destinationId:guid}")]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateDestinationConfiguration(
        Guid tenantId,
        Guid destinationId,
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _tenantConfigurationService.UpdateDestinationConfigurationAsync(
            tenantId,
            destinationId,
            request,
            cancellationToken);

        return Ok(destinationConfiguration);
    }

    [HttpPost("{tenantId:guid}/destinations/{destinationId:guid}/deactivate")]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateDestinationConfiguration(
        Guid tenantId,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _tenantConfigurationService.SetDestinationConfigurationEnabledAsync(
            tenantId,
            destinationId,
            false,
            cancellationToken);

        return Ok(destinationConfiguration);
    }

    [HttpGet("{tenantId:guid}/destinations/{destinationId:guid}/schema")]
    [ProducesResponseType(typeof(DestinationSchemaDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetDestinationSchema(
        Guid tenantId,
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var schema = await _destinationSchemaService.GetSchemaAsync(
            tenantId,
            destinationId,
            cancellationToken);

        return Ok(schema);
    }

    [HttpPost("{tenantId:guid}/mapping-profiles")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddMappingProfile(
        Guid tenantId,
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _tenantConfigurationService.AddMappingProfileAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/mapping-profiles/{mappingProfile.Id}", mappingProfile);
    }

    [HttpPut("{tenantId:guid}/mapping-profiles/{mappingProfileId:guid}")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMappingProfile(
        Guid tenantId,
        Guid mappingProfileId,
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _tenantConfigurationService.UpdateMappingProfileAsync(
            tenantId,
            mappingProfileId,
            request,
            cancellationToken);

        return Ok(mappingProfile);
    }

    [HttpPost("{tenantId:guid}/mapping-profiles/{mappingProfileId:guid}/deactivate")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateMappingProfile(
        Guid tenantId,
        Guid mappingProfileId,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _tenantConfigurationService.SetMappingProfileEnabledAsync(
            tenantId,
            mappingProfileId,
            false,
            cancellationToken);

        return Ok(mappingProfile);
    }

    [HttpPost("{tenantId:guid}/resources")]
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> ConfigureResource(
        Guid tenantId,
        [FromBody] ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _tenantConfigurationService.ConfigureResourceAsync(
            tenantId,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/resources/{resourceConfiguration.Id}", resourceConfiguration);
    }

    [HttpPost("{tenantId:guid}/resources/{resourceType}/deactivate")]
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateResourceConfiguration(
        Guid tenantId,
        string resourceType,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _tenantConfigurationService.SetResourceConfigurationEnabledAsync(
            tenantId,
            resourceType,
            false,
            cancellationToken);

        return Ok(resourceConfiguration);
    }

    [HttpPost("{tenantId:guid}/resources/{resourceType}/routes")]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddResourceRoute(
        Guid tenantId,
        string resourceType,
        [FromBody] CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await _tenantConfigurationService.AddResourceRouteAsync(
            tenantId,
            resourceType,
            request,
            cancellationToken);

        return Created($"/api/v1/tenants/{tenantId}/resources/{resourceType}/routes/{route.Id}", route);
    }

    [HttpPut("{tenantId:guid}/resources/{resourceType}/routes/{routeId:guid}")]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateResourceRoute(
        Guid tenantId,
        string resourceType,
        Guid routeId,
        [FromBody] CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await _tenantConfigurationService.UpdateResourceRouteAsync(
            tenantId,
            resourceType,
            routeId,
            request,
            cancellationToken);

        return Ok(route);
    }

    [HttpPost("{tenantId:guid}/resources/{resourceType}/routes/{routeId:guid}/deactivate")]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateResourceRoute(
        Guid tenantId,
        string resourceType,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        var route = await _tenantConfigurationService.SetResourceRouteEnabledAsync(
            tenantId,
            resourceType,
            routeId,
            false,
            cancellationToken);

        return Ok(route);
    }
}
