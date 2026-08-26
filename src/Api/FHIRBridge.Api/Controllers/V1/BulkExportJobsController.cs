using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Operator-facing view/control over durable <see cref="BulkExportJob"/> checkpoints — the FHIR Bulk Data <c>$export</c>
/// jobs <c>BulkExportPollWorker</c> polls to completion. Read access lets the portal show an in-flight export instead
/// of a route looking stuck on "Running"; cancel implements the spec's <c>DELETE [status url]</c> flow so an operator
/// can stop one instead of waiting out the source server's worst case (per athenahealth's docs, up to ~2 hours).
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/bulk-export-jobs")]
public sealed class BulkExportJobsController : ControllerBase
{
    private readonly IBulkExportJobRepository _jobRepository;
    private readonly IFhirBulkExportClient _bulkExportClient;
    private readonly ISourceConnectionRuntimeResolver _sourceResolver;

    public BulkExportJobsController(
        IBulkExportJobRepository jobRepository,
        IFhirBulkExportClient bulkExportClient,
        ISourceConnectionRuntimeResolver sourceResolver)
    {
        _jobRepository = jobRepository;
        _bulkExportClient = bulkExportClient;
        _sourceResolver = sourceResolver;
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(BulkExportJob), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetAsync(id, cancellationToken);
        return job is null ? NotFound() : Ok(job);
    }

    /// <summary>
    /// Cancels an in-flight export: <c>DELETE</c>s the job's status URL on the source server (per the FHIR Bulk
    /// Data spec — a no-op if the source has nothing left to clean up) and marks the local checkpoint
    /// <c>Cancelled</c> so <c>BulkExportPollWorker</c> stops polling it. A job with no status URl yet (kicked off
    /// but not yet <c>MarkKickedOff</c>'d) or already in a terminal state is cancelled locally only — nothing to
    /// tell the source server about.
    /// </summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken cancellationToken)
    {
        var job = await _jobRepository.GetAsync(id, cancellationToken);
        if (job is null)
        {
            return NotFound();
        }

        if (job.Status is BulkExportJobStatus.Completed or BulkExportJobStatus.Failed or BulkExportJobStatus.Cancelled)
        {
            return Conflict(new { message = $"Bulk export job is already {job.Status.ToLowerInvariant()}." });
        }

        if (!string.IsNullOrWhiteSpace(job.StatusUrl))
        {
            var source = await _sourceResolver.ResolveAsync(
                job.SourceConnectionId, searchParameters: null, targetPatientId: null, cancellationToken);

            // A source that no longer resolves (connection deleted) has nothing reachable on the server side to
            // cancel — the local checkpoint is still marked Cancelled below so the poller stops touching it.
            if (source is not null)
            {
                await _bulkExportClient.CancelExportAsync(job.StatusUrl, source, cancellationToken);
            }
        }

        job.MarkCancelled(DateTime.UtcNow);
        await _jobRepository.UpdateAsync(job, cancellationToken);

        return NoContent();
    }
}
