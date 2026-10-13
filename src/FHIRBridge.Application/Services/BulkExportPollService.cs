using System.Text.Json;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Security;
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
    private readonly IPausedBulkExportRunCloser _pausedRunCloser;
    private readonly IGlobalExceptionManager? _exceptionManager;
    private readonly IAmbientActorContext? _ambientActorContext;
    private readonly FHIRBridge.Runtime.Application.Workflows.Storage.IWorkflowRunStore? _runStore;
    private readonly FHIRBridge.Runtime.Application.Workflows.Storage.IWorkflowDefinitionStore? _definitionStore;
    private readonly ILogger<BulkExportPollService> _logger;

    public BulkExportPollService(
        IBulkExportJobRepository jobRepository,
        IFhirBulkExportClient bulkExportClient,
        ISourceConnectionRuntimeResolver sourceResolver,
        IRankedWorkflowOrchestrator workflowOrchestrator,
        IPausedBulkExportRunCloser pausedRunCloser,
        ILogger<BulkExportPollService> logger,
        IGlobalExceptionManager? exceptionManager = null,
        IAmbientActorContext? ambientActorContext = null,
        FHIRBridge.Runtime.Application.Workflows.Storage.IWorkflowRunStore? runStore = null,
        FHIRBridge.Runtime.Application.Workflows.Storage.IWorkflowDefinitionStore? definitionStore = null)
    {
        _jobRepository = jobRepository;
        _bulkExportClient = bulkExportClient;
        _sourceResolver = sourceResolver;
        _workflowOrchestrator = workflowOrchestrator;
        _pausedRunCloser = pausedRunCloser;
        _logger = logger;
        _exceptionManager = exceptionManager;
        _ambientActorContext = ambientActorContext;
        _runStore = runStore;
        _definitionStore = definitionStore;
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
                // Re-enter the paused run's correlation scope for the duration of this job. Everything the poll
                // does — the status GET, each NDJSON file download, the cleanup DELETE, and the whole resumed
                // continuation (Governance → Transform → Output) — is an outbound call logged by
                // ApiRequestLoggingHandler, which reads the ambient correlation id. Without this the longest and
                // most failure-prone leg of a bulk export is the one leg that cannot be found alongside the run
                // it belongs to. Scoped per job, not per tick: one tick polls many jobs, each with its own run.
                using var correlationScope = _ambientActorContext?.BeginScope("BulkExportPollWorker", job.CorrelationId);

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

                await RecordFailedAttemptAsync(job, exception, defaultPollIntervalSeconds, maxPollAttempts, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Counts a poll that threw — the source's token request refused (revoked or expired credentials, a disabled
    /// client), the network or VPN down, a result file that would not download — as one attempt. Before, such a job was
    /// left exactly as it was: never given a next-poll time, polled again every tick for ever, and its run shown
    /// "Running" for ever, because only an InProgress answer ever counted toward the attempt cap. Now the same cap ends
    /// it, as Failed with the last error, and closes its run.
    /// <para>Only a job still Polling: once Completed, the run belongs to the continuation (see RunContinuationAsync),
    /// and a Failed job has already been closed. Recording the attempt can itself fail (the database is what broke);
    /// that is logged and the job is left for the next tick, as before.</para>
    /// </summary>
    private async Task RecordFailedAttemptAsync(
        BulkExportJob job, Exception exception, int defaultPollIntervalSeconds, int maxPollAttempts, CancellationToken cancellationToken)
    {
        if (job.Status != BulkExportJobStatus.Polling)
        {
            return;
        }

        try
        {
            job.RecordPollAttempt(DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, defaultPollIntervalSeconds)));
            if (job.PollAttemptCount >= maxPollAttempts)
            {
                await FailJobAsync(
                    job,
                    $"Bulk export status could not be read after {job.PollAttemptCount} attempts. Last error: {Snippet(exception.Message)}",
                    cancellationToken);
                return;
            }

            await _jobRepository.UpdateAsync(job, cancellationToken);
        }
        catch (Exception recordException) when (recordException is not OperationCanceledException)
        {
            _logger.LogError(recordException, "Could not record the failed poll attempt for bulk export job {JobId}.", job.Id);
        }
    }

    /// <summary>Same 300-character cap the export client puts on a source's error body: a source can answer with a full
    /// HTML error page, which is noise in a run's error and must not be rendered into the portal.</summary>
    private static string Snippet(string message) => message.Length > 300 ? message[..300] + "…" : message;

    private async Task PollOneJobAsync(
        BulkExportJob job, int defaultPollIntervalSeconds, int maxPollAttempts, CancellationToken cancellationToken)
    {
        var source = await _sourceResolver.ResolveAsync(
            job.SourceConnectionId, searchParameters: null, targetPatientId: null, cancellationToken);
        if (source is null)
        {
            await FailJobAsync(job, $"Source connection '{job.SourceConnectionId}' no longer exists.", cancellationToken);
            return;
        }

        var result = await _bulkExportClient.PollOnceAsync(job.StatusUrl!, source, cancellationToken);

        switch (result.Status)
        {
            case BulkExportPollStatus.InProgress:
                job.RecordPollAttempt(DateTime.UtcNow + (result.RetryAfter ?? TimeSpan.FromSeconds(Math.Max(1, defaultPollIntervalSeconds))));
                if (job.PollAttemptCount >= maxPollAttempts)
                {
                    await FailJobAsync(job, $"Bulk export did not complete after {job.PollAttemptCount} status polls.", cancellationToken);
                    break;
                }

                await _jobRepository.UpdateAsync(job, cancellationToken);
                break;

            case BulkExportPollStatus.Failed:
                await FailJobAsync(job, result.ErrorMessage ?? "Bulk export status poll failed.", cancellationToken);
                break;

            case BulkExportPollStatus.Completed:
                // Honor the user's resource-type selection strictly. The FHIR Bulk Data spec lets a server put
                // resources it considers related to the requested `_type` into the output manifest — eCW returns
                // Binary/Medication/Media/Specimen (referenced by the requested types) even though `_type` asked
                // only for the selected set. Drop those manifest files up front so unrequested types are never
                // downloaded (avoids, e.g., a long slow Binary-attachment tail) or written.
                //
                // One exception: Binary, when this run writes notes back into an EHR. A note's text sits in the
                // Binary its DocumentReference links to, and a bulk-only app's token is refused on a plain
                // Binary/{id} read (eCW "Backend - Bulk API": "No valid token found"), so the export's own Binary
                // file is the only place the write-back can get the text from.
                var alsoKeep = await NeedsNoteContentAsync(job, cancellationToken) ? NoteContentResourceTypes : null;
                var outputFiles = FilterFilesToRequestedResourceTypes(result.Files ?? [], job.RequestedResourceTypesJson, alsoKeep);
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

    /// <summary>Marks the job failed, then closes the workflow run paused on it. A failed job is never polled again,
    /// so without the second step its run stayed AwaitingBulkExport for ever — "Running" on Execution History and in
    /// the Bulk Data Status popup, with no event to update either. Job first: if closing the run then throws, the
    /// exception reaches PollDueJobsAsync's per-job capture rather than being swallowed here.</summary>
    private async Task FailJobAsync(BulkExportJob job, string errorMessage, CancellationToken cancellationToken)
    {
        job.MarkFailed(errorMessage, DateTime.UtcNow);
        await _jobRepository.UpdateAsync(job, cancellationToken);

        if (job.SourcePath == BulkExportJobSourcePath.WorkflowNode && job.WorkflowRunId is { } workflowRunId)
        {
            await _pausedRunCloser.FailAsync(workflowRunId, $"Bulk export failed: {errorMessage}", cancellationToken);
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
                try
                {
                    await _workflowOrchestrator.ResumeAfterBulkExportAsync(
                        job.WorkflowRunId!.Value, job.WorkflowNodeId!.Value, job.PriorNodeOutputsJson, job.ContextJson,
                        resources, skippedResourceTypeReasons, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // A run that fails while running is closed by the orchestrator itself. One whose resume threw before
                    // it restarted (its workflow deleted, its saved context unreadable) was left AwaitingBulkExport for
                    // ever: the job is Completed, so nothing polls it again. The closer acts only on a run still
                    // AwaitingBulkExport, so the first case is left as the orchestrator saved it.
                    await _pausedRunCloser.FailAsync(
                        job.WorkflowRunId!.Value,
                        $"The bulk export completed, but the run could not be resumed: {Snippet(exception.Message)}",
                        CancellationToken.None);
                    throw;
                }

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
    private static readonly IReadOnlyCollection<string> NoteContentResourceTypes = ["Binary"];

    /// <summary>
    /// True when the paused run's workflow writes back into an EHR and asked for notes (DocumentReference), so the
    /// export's Binary file carries note text the write-back needs. False whenever that cannot be established: the
    /// default is the strict filter, which never downloads more than was asked for.
    /// </summary>
    private async Task<bool> NeedsNoteContentAsync(BulkExportJob job, CancellationToken cancellationToken)
    {
        if (_runStore is null
            || _definitionStore is null
            || job.SourcePath != BulkExportJobSourcePath.WorkflowNode
            || job.WorkflowRunId is not { } runId
            || ParseRequestedResourceTypeSet(job.RequestedResourceTypesJson) is not { } requested
            || !requested.Contains("DocumentReference"))
        {
            return false;
        }

        try
        {
            var run = await _runStore.GetAsync(runId, cancellationToken);
            var workflow = run is null ? null : await _definitionStore.GetAsync(run.WorkflowDefinitionId, cancellationToken);
            return workflow is not null
                && FHIRBridge.Runtime.Application.Workflows.Catalog.WorkflowNodeTypes.HasEhrWriteBack(workflow);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Could not tell whether bulk export job {JobId} feeds an EHR write-back; Binary files are skipped.", job.Id);
            return false;
        }
    }

    private IReadOnlyList<BulkExportFile> FilterFilesToRequestedResourceTypes(
        IReadOnlyList<BulkExportFile> files, string? requestedResourceTypesJson, IReadOnlyCollection<string>? alsoKeep = null)
    {
        var requested = ParseRequestedResourceTypeSet(requestedResourceTypesJson);
        if (requested is null || files.Count == 0)
        {
            return files;
        }

        if (alsoKeep is { Count: > 0 })
        {
            requested = new HashSet<string>(requested.Concat(alsoKeep), requested.Comparer);
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
