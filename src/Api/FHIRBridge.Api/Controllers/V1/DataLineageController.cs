using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Governance;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Data Lineage: source field → mapping rule → destination column → export. The tree structure
/// (<see cref="GetLineage"/>) is PHI-free metadata, gated by the same governance.read permission as every other
/// governance screen. There is no longer any endpoint that returns a field's actual value: the resource content
/// those values came from is not retained at all, so there is nothing to reveal.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/governance/data-lineage")]
public sealed class DataLineageController : ControllerBase
{
    private readonly IDataLineageService _dataLineageService;
    private readonly IGovernanceLogger _governanceLogger;

    public DataLineageController(IDataLineageService dataLineageService, IGovernanceLogger governanceLogger)
    {
        _dataLineageService = dataLineageService;
        _governanceLogger = governanceLogger;
    }

    [HttpGet("{resourceRecordId:guid}")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View data lineage structure (source field, mapping rule, destination column, export — no values).")]
    [ProducesResponseType(typeof(DataLineageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLineage(Guid resourceRecordId, CancellationToken cancellationToken)
    {
        var result = await _dataLineageService.GetLineageAsync(resourceRecordId, cancellationToken);
        return result is null ? NotFound() : Ok(result);
    }
}
