using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Read/delete endpoints for the <c>SourceConnection</c> configuration entity. Listing already lives on
/// <see cref="ConfigurationCatalogController"/> (<c>GET /api/v1/source-connections</c>) and create/update stay on
/// <see cref="ConfigurationsController"/> (their permission depends on the connection's vendor, resolved at
/// runtime) — this controller only adds the two operations neither of those expose yet: fetch a single source
/// connection by id, and delete one. Gated by the flat (vendor-independent) SourceConnections/View and
/// SourceConnections/Delete permissions the Source Connections admin page checks on the frontend.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/source-connections")]
public sealed class SourceConnectionsController : ControllerBase
{
    private readonly IConfigurationService _configurationService;

    public SourceConnectionsController(IConfigurationService configurationService)
    {
        _configurationService = configurationService;
    }

    [HttpGet("{sourceConnectionId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.GetSourceConnectionByIdAsync(sourceConnectionId, cancellationToken);
        return sourceConnection is null ? NotFound() : Ok(sourceConnection);
    }

    [HttpDelete("{sourceConnectionId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Delete, description: "Delete a source connection.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        await _configurationService.DeleteSourceConnectionAsync(sourceConnectionId, cancellationToken);
        return NoContent();
    }
}
