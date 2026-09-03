using System.Text.Json;
using System.Text.RegularExpressions;
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
                // Honor the user's resource-type selection strictly. The FHIR Bulk Data spec lets a server put
                // resources it considers related to the requested `_type` into the output manifest — eCW returns
                // Binary/Medication/Media/Specimen (referenced by the requested types) even though `_type` asked
                // only for the selected set. Drop those manifest files up front so unrequested types are never
                // downloaded (avoids, e.g., a long slow Binary-attachment tail) or written.
                var outputFiles = FilterFilesToRequestedResourceTypes(result.Files ?? [], job.RequestedResourceTypesJson);
                var resources = await _bulkExportClient.DownloadResultsAsync(outputFiles, source, cancellationToken);

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
                        var reasons = partialFailures
                            .Select(failure => failure.Code is { Length: > 0 } code
                                ? $"{failure.Diagnostics} (OperationOutcome: {failure.Severity ?? "error"}/{code})"
                                : failure.Diagnostics)
                            .ToList();

                        skippedResourceTypeReasons = FilterToRequestedResourceTypes(reasons, job.RequestedResourceTypesJson);
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

    // A Group export scoped to a lone "Patient" resource type omits `_type` entirely to dodge an Epic Interconnect
    // bug (see BulkExportScopes.ResolveTypeParameter) — the server then attempts every resource type it supports,
    // most of which the node never asked for and isn't authorized/configured to fetch. Without this filter, every
    // one of those unrequested rejections surfaces to the caller as a "partial success" failure even though nothing
    // the workflow actually needed was missing. requestedResourceTypesJson is only set for jobs created after this
    // filter shipped; older/other-source-path jobs have none, so every reason is kept unfiltered (today's behavior).
    // Keeps only the manifest output files whose resource type the run actually requested. Guarded so it can never
    // turn a real export into an empty one: with no requested-type list, or when NOTHING matches (e.g. a server that
    // labels every output file generically), the full file set is returned unchanged and the record/destination-side
    // filters still apply downstream.
    private IReadOnlyList<BulkExportFile> FilterFilesToRequestedResourceTypes(
        IReadOnlyList<BulkExportFile> files, string? requestedResourceTypesJson)
    {
        var requested = ParseRequestedResourceTypeSet(requestedResourceTypesJson);
        if (requested is null || files.Count == 0)
        {
            return files;
        }

        var kept = files.Where(file => requested.Contains(file.ResourceType)).ToList();
        if (kept.Count == 0)
        {
            _logger.LogWarning(
                "Bulk export manifest listed {FileCount} output file(s) but none matched the requested resource types " +
                "[{Requested}] (files typed [{FileTypes}]); downloading all files rather than nothing.",
                files.Count, string.Join(", ", requested), string.Join(", ", files.Select(f => f.ResourceType).Distinct()));
            return files;
        }

        if (kept.Count < files.Count)
        {
            _logger.LogInformation(
                "Bulk export manifest filter: keeping {Kept}/{Total} output file(s); skipping unrequested resource " +
                "type(s) [{Dropped}] the server included beyond the requested _type.",
                kept.Count, files.Count,
                string.Join(", ", files.Where(f => !requested.Contains(f.ResourceType)).Select(f => f.ResourceType).Distinct()));
        }

        return kept;
    }

    private static HashSet<string>? ParseRequestedResourceTypeSet(string? requestedResourceTypesJson)
    {
        if (string.IsNullOrWhiteSpace(requestedResourceTypesJson))
        {
            return null;
        }

        try
        {
            var types = JsonSerializer.Deserialize<List<string>>(requestedResourceTypesJson);
            return types is { Count: > 0 } ? new HashSet<string>(types, StringComparer.OrdinalIgnoreCase) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string> FilterToRequestedResourceTypes(
        IReadOnlyList<string> reasons, string? requestedResourceTypesJson)
    {
        if (string.IsNullOrWhiteSpace(requestedResourceTypesJson))
        {
            return reasons;
        }

        List<string>? requestedResourceTypes;
        try
        {
            requestedResourceTypes = JsonSerializer.Deserialize<List<string>>(requestedResourceTypesJson);
        }
        catch (JsonException)
        {
            return reasons;
        }

        if (requestedResourceTypes is not { Count: > 0 })
        {
            return reasons;
        }

        return reasons
            .Where(reason => requestedResourceTypes.Any(type =>
                Regex.IsMatch(reason, $@"\b{Regex.Escape(type)}\b", RegexOptions.IgnoreCase)))
            .ToList();
    }
}
