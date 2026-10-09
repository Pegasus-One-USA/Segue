using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// The EHR write-back review list (<see cref="IEhrWriteLedgerReviewService"/>): writes whose outcome is unknown,
/// that the EHR refused, or that were abandoned mid-send. Rows hold no PHI. Resolving one is an Edit on the EHR
/// Write-Back permission group, because releasing a row lets the next run send it to the EHR again.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/ehr-write-ledger")]
public sealed class EhrWriteLedgerController : ControllerBase
{
    private readonly IEhrWriteLedgerReviewService _review;

    public EhrWriteLedgerController(IEhrWriteLedgerReviewService review)
    {
        _review = review;
    }

    [HttpGet("review")]
    [StandardPermission(PermissionGroupCode.EhrWriteBack, PermissionActionCode.View, description: "View EHR write-back writes awaiting review.")]
    [ProducesResponseType(typeof(PagedResult<EhrWriteLedgerReviewItemDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListForReview(
        [FromQuery] string? resourceType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25,
        CancellationToken cancellationToken = default) =>
        Ok(await _review.ListAsync(resourceType, page, pageSize, cancellationToken));

    [HttpPost("{id:guid}/mark-written")]
    [StandardPermission(PermissionGroupCode.EhrWriteBack, PermissionActionCode.Edit, description: "Record that a write awaiting review is in the EHR.")]
    [ProducesResponseType(typeof(EhrWriteLedgerReviewItemDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> MarkWritten(
        Guid id,
        [FromBody] ResolveEhrWriteAsWrittenRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _review.ResolveAsWrittenAsync(id, request.TargetResourceId, cancellationToken));

    [HttpPost("{id:guid}/release")]
    [StandardPermission(PermissionGroupCode.EhrWriteBack, PermissionActionCode.Edit, description: "Release a write awaiting review so the next run sends it again.")]
    [ProducesResponseType(typeof(EhrWriteLedgerReviewItemDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Release(Guid id, CancellationToken cancellationToken) =>
        Ok(await _review.ReleaseForResendAsync(id, cancellationToken));
}
