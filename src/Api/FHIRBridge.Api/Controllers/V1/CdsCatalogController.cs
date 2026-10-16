using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Cds;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>Read-only CDS Hooks admin views: handler types, the URLs to register with an EHR, and call activity.</summary>
[ApiController]
[Authorize]
[Route("api/v1/cds")]
public sealed class CdsCatalogController : ControllerBase
{
    private readonly ICdsCatalogService _service;

    public CdsCatalogController(ICdsCatalogService service)
    {
        _service = service;
    }

    [HttpGet("handlers")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(IReadOnlyList<CdsHandlerDto>), StatusCodes.Status200OK)]
    public IActionResult Handlers() => Ok(_service.GetHandlers());

    [HttpGet("discovery-info")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsDiscoveryInfoDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DiscoveryInfo(CancellationToken cancellationToken) =>
        Ok(await _service.GetDiscoveryInfoAsync($"{Request.Scheme}://{Request.Host}{Request.PathBase}", cancellationToken));

    /// <summary>CDS calls, newest first. Metadata only — no patient data is ever recorded.</summary>
    /// <param name="serviceId">Exact service id (URL slug).</param>
    [HttpGet("activity")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsActivityPageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Activity(
        [FromQuery] string? serviceId,
        [FromQuery] CdsCallKind? kind,
        [FromQuery] CdsCallOutcome? outcome,
        [FromQuery] DateTime? fromUtc,
        [FromQuery] DateTime? toUtc,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var filter = new CdsCallLogFilter(serviceId, kind, outcome, fromUtc?.ToUniversalTime(), toUtc?.ToUniversalTime());
        var result = await _service.GetActivityAsync(
            filter, page <= 0 ? 1 : page, pageSize <= 0 ? 25 : pageSize, cancellationToken);
        return Ok(result);
    }
}
