using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// CRUD for <c>SourceConfiguration</c> — the workflow-specific retrieval/scopes config that references a reusable
/// <see cref="Application.DTOs.SourceConnectionDto"/> (see docs/backend/13-source-connection-configuration-split-plan.md).
/// Gated by the same flat SourceConnections permissions as <see cref="SourceConnectionsController"/> since this is a
/// sub-resource of a source connection, not a distinct vendor-dependent concept.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/source-configurations")]
public sealed class SourceConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configurationService;

    public SourceConfigurationsController(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [ProducesResponseType(typeof(IReadOnlyList<SourceConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(CancellationToken cancellationToken)
    {
        var configurations = await _configurationService.GetSourceConfigurationsAsync(cancellationToken);
        return Ok(configurations);
    }

    [HttpGet("{sourceConfigurationId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [ProducesResponseType(typeof(SourceConfigurationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid sourceConfigurationId, CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.GetSourceConfigurationByIdAsync(sourceConfigurationId, cancellationToken);
        return configuration is null ? NotFound() : Ok(configuration);
    }

    [HttpPost]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Create, description: "Create a source connection.")]
    [ProducesResponseType(typeof(SourceConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateSourceConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.AddSourceConfigurationAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetById), new { sourceConfigurationId = configuration.Id }, configuration);
    }

    [HttpPut("{sourceConfigurationId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Edit, description: "Edit a source connection.")]
    [ProducesResponseType(typeof(SourceConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        Guid sourceConfigurationId,
        [FromBody] CreateSourceConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var configuration = await _configurationService.UpdateSourceConfigurationAsync(sourceConfigurationId, request, cancellationToken);
        return Ok(configuration);
    }

    [HttpDelete("{sourceConfigurationId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Delete, description: "Delete a source connection.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid sourceConfigurationId, CancellationToken cancellationToken)
    {
        await _configurationService.DeleteSourceConfigurationAsync(sourceConfigurationId, cancellationToken);
        return NoContent();
    }
}
