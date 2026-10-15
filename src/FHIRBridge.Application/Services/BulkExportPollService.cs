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
            var progress = new PollProgress(job.PollAttemptCount);
            try
            {
                // Re-enter the paused run's correlation scope for the duration of this job. Everything the poll
                // does — the status GET, each NDJSON file download, the cleanup DELETE, and the whole resumed
                // continuation (Governance → Transform → Output) — is an outbound call logged by
                // ApiRequestLoggingHandler, which reads the ambient correlation id. Without this the longest and
                // most failure-prone leg of a bulk export is the one leg that cannot be found alongside the run
                // it belongs to. Scoped per job, not per tick: one tick polls many jobs, each with its own run.
                using var correlationScope = _ambientActorContext?.BeginScope("BulkExportPollWorker", job.CorrelationId);

                await PollOneJobAsync(job, progress, defaultPollIntervalSeconds, maxPollAttempts, cancellationToken);
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

                await RecordFailedAttemptAsync(job, progress, exception, defaultPollIntervalSeconds, maxPollAttempts, cancellationToken);
            }
        }
    }

    /// <summary>How far one job's poll got this tick, so a failure is counted and retried for what actually broke.</summary>
    private sealed class PollProgress
    {
        public PollProgress(int attemptCountAtStart)
        {
            AttemptCountAtStart = attemptCountAtStart;
        }

        public int AttemptCountAtStart { get; }

        /// <summary>The source reported the export complete; a failure after this is in fetching its results.</summary>
        public bool ExportCompleted { get; set; }
    }

    /// <summary>The wait before retrying the downloads of an export the source has already finished. A failed download
    /// (a timeout, a file server briefly down) is no reason to give up on an export that succeeded, so this is longer
    /// than the status-poll interval: the attempt cap then spans hours rather than minutes before the job fails.</summary>
    private static readonly TimeSpan DownloadRetryDelay = TimeSpan.FromMinutes(5);

    /// <summary>Attempts a completed export's downloads get ON TOP of the poll cap: at <see cref="DownloadRetryDelay"/> each,
    /// about two hours of retries, however many status polls the export needed before the source finished it.</summary>
    public const int DownloadAttemptBudget = 24;

    /// <summary>
    /// Counts a poll that threw — the source's token request refused (revoked or expired credentials, a disabled
    /// client), the network or VPN down, a result file that would not download — as one attempt. Before, such a job was
    /// left exactly as it was: never given a next-poll time, polled again every tick for ever, and its run shown
    /// "Running" for ever, because only an InProgress answer ever counted toward the attempt cap. Now the same cap ends
    /// it, as Failed with the last error, and closes its run.
    /// <para>Only a job still Polling: once Completed, the run belongs to the continuation (see RunContinuationAsync),
    /// and a Failed job has already been closed. Recording the attempt can itself fail (the database is what broke);
    /// that is logged and the job is left for the next tick, as before.</para>
    /// <para>One tick counts at most one attempt: when the InProgress branch already recorded this tick's attempt and
    /// then its save (or closing the run at the cap) threw, the attempt is not recorded again, only saved. A failure
    /// after the source reported the export complete — the result downloads — is retried after
    /// <see cref="DownloadRetryDelay"/> and named as a download failure if the cap ends it.</para>
    /// </summary>
    private async Task RecordFailedAttemptAsync(
        BulkExportJob job, PollProgress progress, Exception exception, int defaultPollIntervalSeconds, int maxPollAttempts,
        CancellationToken cancellationToken)
    {
        if (job.Status != BulkExportJobStatus.Polling)
        {
            return;
        }

        try
        {
            if (job.PollAttemptCount == progress.AttemptCountAtStart)
            {
                var retryDelay = progress.ExportCompleted
                    ? DownloadRetryDelay
                    : TimeSpan.FromSeconds(Math.Max(1, defaultPollIntervalSeconds));
                job.RecordPollAttempt(DateTime.UtcNow + retryDelay);
            }

            // Once the source has reported the export complete, its downloads get DownloadAttemptBudget attempts on
            // top of the cap, so an export that needed most of the cap in status polls is not failed on its first
            // download error. The count is not reset: the source reports the export complete again on every retry, so
            // the higher cap applies each time, and the budget still ends it.
            var attemptCap = progress.ExportCompleted ? maxPollAttempts + DownloadAttemptBudget : maxPollAttempts;
            if (job.PollAttemptCount >= attemptCap)
            {
                var failure = progress.ExportCompleted
                    ? $"The bulk export completed, but its results could not be downloaded after {job.PollAttemptCount} attempts."
                    : $"Bulk export status could not be read after {job.PollAttemptCount} attempts.";
                await FailJobAsync(job, $"{failure} Last error: {exception.Message}", cancellationToken);
                return;
            }

            await _jobRepository.UpdateAsync(job, cancellationToken);
        }
        catch (Exception recordException) when (recordException is not OperationCanceledException)
        {
            _logger.LogError(recordException, "Could not record the failed poll attempt for bulk export job {JobId}.", job.Id);
        }
    }

    private static readonly Regex UrlPattern = new(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MarkupPattern = new(@"<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// The form of a failure reason that may leave the poller. The text becomes the job's error, the run's error and its
    /// Errors entry, the Bulk Data Status popup's reason, and the run-status event every connected browser receives — so
    /// it must carry nothing a user may not see. A failed download's message names the file URL, and for a source whose
    /// file URLs are pre-signed (manifest requiresAccessToken: false) that URL itself grants access to the bulk NDJSON;
    /// a source's error body can echo URLs and is often a whole HTML or XML page.
    /// <para>So every URL is cut to its scheme and host, markup is dropped, whitespace (newlines included) collapsed,
    /// and the result capped at 300 characters, the same cap the export client puts on a source's error body. The full
    /// text stays in the logs, where PollDueJobsAsync records the exception.</para>
    /// </summary>
    internal static string PublicReason(string message)
    {
        var withoutUrls = UrlPattern.Replace(message, match =>
            Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)
                ? uri.GetLeftPart(UriPartial.Authority)
                : "[link removed]");
        var plain = WhitespacePattern.Replace(MarkupPattern.Replace(withoutUrls, " "), " ").Trim();
        return plain.Length > 300 ? plain[..300] + "…" : plain;
    }

    private async Task PollOneJobAsync(
        BulkExportJob job, PollProgress progress, int defaultPollIntervalSeconds, int maxPollAttempts, CancellationToken cancellationToken)
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
                progress.ExportCompleted = true;

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

    /// <summary>Closes the workflow run paused on the job, then marks the job failed. A failed job is never polled
    /// again, so without the first step its run stayed AwaitingBulkExport for ever — "Running" on Execution History and
    /// in the Bulk Data Status popup, with no event to update either.
    /// <para>Run first, so the sequence can be retried: if closing the run throws (a transient store error), the job is
    /// still Polling, PollDueJobsAsync counts the attempt, and the next tick does both again. The other way round, a
    /// terminal job with a still-paused run was never revisited. If the run closes but the job's save then fails, the
    /// next tick re-polls, closing the run is a no-op, and the job is saved; should the source report the export
    /// completed after all by then, ResumeAfterBulkExportAsync leaves the closed run alone.</para></summary>
    private async Task FailJobAsync(BulkExportJob job, string errorMessage, CancellationToken cancellationToken)
    {
        // The job's error and the run's error are user-facing: see PublicReason.
        errorMessage = PublicReason(errorMessage);

        if (job.SourcePath == BulkExportJobSourcePath.WorkflowNode && job.WorkflowRunId is { } workflowRunId)
        {
            await _pausedRunCloser.FailAsync(workflowRunId, $"Bulk export failed: {errorMessage}", cancellationToken);
        }

        job.MarkFailed(errorMessage, DateTime.UtcNow);
        await _jobRepository.UpdateAsync(job, cancellationToken);
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
                    try
                    {
                        var closed = await _pausedRunCloser.FailAsync(
                            job.WorkflowRunId!.Value,
                            PublicReason($"The bulk export completed, but the run could not be resumed: {exception.Message}"),
                            CancellationToken.None);
                        if (!closed)
                        {
                            // Usually the orchestrator closed it already. But a run left Running — its pause was never
                            // saved — whose resume threw before any node ran is closed by nobody: say so, so it can be
                            // found, instead of it showing Running for ever with no trace.
                            _logger.LogError(
                                "Workflow run {WorkflowRunId} was not closed after its resume failed: it was no longer awaiting its bulk export. "
                                + "If it still shows Running, it is stuck and must be closed by hand.",
                                job.WorkflowRunId);
                        }
                    }
                    catch (Exception closeException) when (closeException is not OperationCanceledException)
                    {
                        // Logged here and NOT thrown: the resume's own exception (rethrown below) is the one that says
                        // what went wrong, and replacing it with this one recorded the wrong error.
                        _logger.LogError(closeException,
                            "Could not close workflow run {WorkflowRunId} after its resume failed; it stays AwaitingBulkExport.",
                            job.WorkflowRunId);
                    }

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
