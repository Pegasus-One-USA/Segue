using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Application.Services;

public sealed class BulkExportPollService : IBulkExportPollService
{
    private readonly IBulkExportJobRepository _jobRepository;
    private readonly IFhirBulkExportClient _bulkExportClient;
    private readonly ISourceConnectionRuntimeResolver _sourceResolver;
    private readonly IRankedWorkflowOrchestrator _workflowOrchestrator;
    private readonly IGlobalExceptionManager? _exceptionManager;
    private readonly ILogger<BulkExportPollService> _logger;

    public BulkExportPollService(
        IBulkExportJobRepository jobRepository,
        IFhirBulkExportClient bulkExportClient,
        ISourceConnectionRuntimeResolver sourceResolver,
        IRankedWorkflowOrchestrator workflowOrchestrator,
        ILogger<BulkExportPollService> logger,
        IGlobalExceptionManager? exceptionManager = null)
    {
        _jobRepository = jobRepository;
        _bulkExportClient = bulkExportClient;
        _sourceResolver = sourceResolver;
        _workflowOrchestrator = workflowOrchestrator;
        _logger = logger;
        _exceptionManager = exceptionManager;
    }

    public async Task PollDueJobsAsync(
        int maxBatchSize, int defaultPollIntervalSeconds, int maxPollAttempts, CancellationToken cancellationToken)
    {
        var jobs = await _jobRepository.GetPollableAsync(DateTime.UtcNow, maxBatchSize, cancellationToken);

        foreach (var job in jobs)
        {
            // Isolated per job: one job's failure (a stale SourceConnection, a transient network error, a losing
            // optimistic-concurrency race against another Worker instance) must never stop the rest of the batch —
            // same isolation principle ScheduleDispatcherWorker applies at the tick level, just at job granularity.
            try
            {
                await PollOneJobAsync(job, defaultPollIntervalSeconds, maxPollAttempts, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Bulk export poll failed for job {JobId}.", job.Id);

                if (_exceptionManager is not null)
                {
                    await _exceptionManager.CaptureAsync(
                        exception,
                        new ExceptionContext(Module: "Bulk Export Poll", CorrelationId: job.CorrelationId),
                        CancellationToken.None);
                }
            }
        }
    }

    private async Task PollOneJobAsync(
        BulkExportJob job, int defaultPollIntervalSeconds, int maxPollAttempts, CancellationToken cancellationToken)
    {
        var source = await _sourceResolver.ResolveAsync(
            job.SourceConnectionId, searchParameters: null, targetPatientId: null, cancellationToken);
        if (source is null)
        {
            job.MarkFailed($"Source connection '{job.SourceConnectionId}' no longer exists.", DateTime.UtcNow);
            await _jobRepository.UpdateAsync(job, cancellationToken);
            return;
        }

        var result = await _bulkExportClient.PollOnceAsync(job.StatusUrl!, source, cancellationToken);

        switch (result.Status)
        {
            case BulkExportPollStatus.InProgress:
                job.RecordPollAttempt(DateTime.UtcNow + (result.RetryAfter ?? TimeSpan.FromSeconds(Math.Max(1, defaultPollIntervalSeconds))));
                if (job.PollAttemptCount >= maxPollAttempts)
                {
                    job.MarkFailed($"Bulk export did not complete after {job.PollAttemptCount} status polls.", DateTime.UtcNow);
                }

                await _jobRepository.UpdateAsync(job, cancellationToken);
                break;

            case BulkExportPollStatus.Failed:
                job.MarkFailed(result.ErrorMessage ?? "Bulk export status poll failed.", DateTime.UtcNow);
                await _jobRepository.UpdateAsync(job, cancellationToken);
                break;

            case BulkExportPollStatus.Completed:
                var resources = await _bulkExportClient.DownloadResultsAsync(result.Files ?? [], source, cancellationToken);

                // Partial success per the FHIR Bulk Data spec: the job as a whole completed (200), but the
                // manifest's `error` array lists resource types the server excluded (e.g. not supported/authorized
                // for this client's registration) — download and format those alongside the real output so the run
                // finishes as PartialSuccess instead of silently missing that data with no explanation anywhere.
                IReadOnlyList<string>? skippedResourceTypeReasons = null;
                if (result.ErrorFiles is { Count: > 0 } errorFiles)
                {
                    var partialFailures = await _bulkExportClient.DownloadPartialFailuresAsync(errorFiles, source, cancellationToken);
                    if (partialFailures.Count > 0)
                    {
                        skippedResourceTypeReasons = partialFailures
                            .Select(failure => failure.Code is { Length: > 0 } code
                                ? $"{failure.Diagnostics} (OperationOutcome: {failure.Severity ?? "error"}/{code})"
                                : failure.Diagnostics)
                            .ToList();
                    }
                }

                // Mark Completed and persist BEFORE running the continuation: a crash mid-continuation must not
                // cause a re-poll of a $export job the source server has already finished (and may no longer
                // serve) — any retry of a failed continuation belongs at the PipelineRun/WorkflowRun level, not here.
                job.MarkCompleted(DateTime.UtcNow);
                await _jobRepository.UpdateAsync(job, cancellationToken);

                await RunContinuationAsync(job, resources, skippedResourceTypeReasons, cancellationToken);
                break;
        }
    }

    private async Task RunContinuationAsync(
        BulkExportJob job,
        IReadOnlyList<FHIRBridge.Runtime.Domain.ValueObjects.ResourceEnvelope> resources,
        IReadOnlyList<string>? skippedResourceTypeReasons,
        CancellationToken cancellationToken)
    {
        switch (job.SourcePath)
        {
            case BulkExportJobSourcePath.WorkflowNode:
                await _workflowOrchestrator.ResumeAfterBulkExportAsync(
                    job.WorkflowRunId!.Value, job.WorkflowNodeId!.Value, job.PriorNodeOutputsJson, job.ContextJson,
                    resources, skippedResourceTypeReasons, cancellationToken);
                break;

            default:
                // Orchestrator/ConfiguredPipeline bulk-export jobs are not produced by anything yet (that wiring is
                // a separate, follow-up change) — nothing should ever reach here today; captured defensively.
                _logger.LogError("Bulk export job {JobId} completed with unsupported SourcePath '{SourcePath}'.", job.Id, job.SourcePath);
                if (_exceptionManager is not null)
                {
                    await _exceptionManager.CaptureExpectedAsync(
                        new ExpectedFailure("UnsupportedBulkExportSourcePath", $"SourcePath '{job.SourcePath}' has no continuation wired."),
                        new ExceptionContext(Module: "Bulk Export Poll", CorrelationId: job.CorrelationId),
                        CancellationToken.None);
                }

                break;
        }
    }
}
