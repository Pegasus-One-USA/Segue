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
/// governance screen. Revealing an actual field's decrypted value (<see cref="RevealFieldValue"/>) requires the
/// stricter payload.view permission and writes its own DataAccessLog entry per view — never a silent decrypt.
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

    /// <summary>Decrypts and returns exactly one mapped field's actual value — the only endpoint in Data
    /// Lineage that touches PHI. Every call writes its own DataAccessLog entry.</summary>
    [HttpGet("{resourceRecordId:guid}/fields/{targetField}/reveal")]
    [StandardPermission(PermissionGroupCode.Payload, PermissionActionCode.View, description: "Reveal an actual (decrypted) field value in Data Lineage.")]
    [ProducesResponseType(typeof(LineageFieldValueDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> RevealFieldValue(Guid resourceRecordId, string targetField, CancellationToken cancellationToken)
    {
        var result = await _dataLineageService.RevealFieldValueAsync(resourceRecordId, targetField, cancellationToken);

        await _governanceLogger.LogDataAccessAsync(
            new DataAccessEntry(
                "DataLineageField",
                resourceRecordId.ToString(),
                "Revealed",
                Purpose: $"Field: {targetField}"),
            cancellationToken);

        return Ok(result);
    }
}
