using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Governance;
using FHIRBridge.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Wraps any <see cref="IConfiguredDestinationWriter"/> with start/complete/fail structured logging. Applied once in
/// <see cref="ConfiguredDestinationWriterFactory.Create"/>, so every destination type gets identical write telemetry
/// without each of the ~26 registered writers repeating it — and a destination added later is instrumented the moment
/// it is registered, with nothing to remember.
/// <para>
/// Only counts, identifiers and timings are logged. Records themselves never are: a
/// <see cref="MappedDestinationRecord"/> holds mapped patient data, and the PHI-masking Serilog enricher is a
/// backstop for accidents, not a licence to hand it PHI deliberately.
/// </para>
/// <para>
/// Also writes the same outcome to <c>DestinationActivityLogs</c> via <see cref="IGovernanceLogger"/>, which is what
/// puts destinations into Correlation Search at all: the API Requests trace is produced by an <c>HttpClient</c>
/// handler, so it can only ever see destinations that speak HTTP. Because this decorator wraps every registration,
/// one Complete/Failed row is guaranteed for every destination type — including the file/stream writers that have no
/// connection to report. The finer-grained Connect stage comes from the writers themselves, through
/// <see cref="PipelineWriteContext.ReportStageAsync"/>, which this class supplies.
/// </para>
/// </summary>
public sealed class LoggingConfiguredDestinationWriter : IConfiguredDestinationWriter
{
    private readonly IConfiguredDestinationWriter _inner;
    private readonly ILogger _logger;
    private readonly IGovernanceLogger? _governanceLogger;

    public LoggingConfiguredDestinationWriter(
        IConfiguredDestinationWriter inner,
        ILogger logger,
        IGovernanceLogger? governanceLogger = null)
    {
        _inner = inner;
        _logger = logger;
        _governanceLogger = governanceLogger;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();

        // Scoped rather than repeated per message so the failure log below — and anything the writer itself logs
        // in between — carries the same destination identity without restating it.
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["DestinationId"] = destination.Id,
            ["DestinationName"] = destination.Name,
            ["DestinationType"] = destination.DestinationType,
            ["MappingProfileId"] = mappingProfile.Id,
            ["ResourceType"] = mappingProfile.ResourceType,
            ["RouteName"] = context.RouteName,
            ["CorrelationId"] = context.CorrelationId,
        });

        _logger.LogDebug(
            LogEvents.DestinationWriteStarted,
            "Writing {RecordCount} {ResourceType} record(s) to {DestinationType} destination '{DestinationName}' " +
            "via writer {WriterType}.",
            records.Count, mappingProfile.ResourceType, destination.DestinationType, destination.Name,
            _inner.GetType().Name);

        // Hand the writer a stage hook so a connect it performs inside WriteAsync — which this decorator cannot
        // observe — lands in the same governance table as the outcome below, already stamped with this
        // destination's identity so the writer never has to restate it. Chained, not overwritten: a caller that
        // supplied its own hook still gets it.
        var callerReportStageAsync = context.ReportStageAsync;
        var instrumentedContext = context with
        {
            ReportStageAsync = async (report, stageToken) =>
            {
                await LogActivityAsync(
                    destination, mappingProfile, context,
                    report.Stage, report.Status,
                    recordCount: null, writtenCount: null,
                    report.DurationMs, report.Detail, report.Error,
                    stageToken);

                if (callerReportStageAsync is not null)
                {
                    await callerReportStageAsync(report, stageToken);
                }
            },
        };

        try
        {
            var result = await _inner.WriteAsync(destination, mappingProfile, records, instrumentedContext, cancellationToken);

            var recordErrorCount = result.RecordErrors?.Count ?? 0;
            if (recordErrorCount > 0)
            {
                // Per-record rejections don't throw — the write "succeeds" while silently dropping rows. Logged at
                // Warning so a partially-written batch is visible without having to diff source and destination counts.
                _logger.LogWarning(
                    LogEvents.DestinationWriteCompleted,
                    "Wrote {WrittenCount} of {RecordCount} {ResourceType} record(s) to {DestinationType} destination " +
                    "'{DestinationName}' in {ElapsedMs}ms — {RecordErrorCount} record(s) were rejected. FirstError={FirstRecordError}",
                    result.Count, records.Count, mappingProfile.ResourceType, destination.DestinationType,
                    destination.Name, ElapsedMs(startedAt), recordErrorCount, result.RecordErrors![0]);

                await LogActivityAsync(
                    destination, mappingProfile, context,
                    DestinationStageNames.Complete, DestinationStageStatuses.PartialSuccess,
                    records.Count, result.Count, ElapsedMs(startedAt),
                    detail: null, error: result.RecordErrors[0], cancellationToken);
            }
            else
            {
                _logger.LogInformation(
                    LogEvents.DestinationWriteCompleted,
                    "Wrote {WrittenCount} of {RecordCount} {ResourceType} record(s) to {DestinationType} destination " +
                    "'{DestinationName}' in {ElapsedMs}ms.",
                    result.Count, records.Count, mappingProfile.ResourceType, destination.DestinationType,
                    destination.Name, ElapsedMs(startedAt));

                // NoData rather than Succeeded when the batch was empty: "wrote 0 of 0 records" is a routine
                // outcome (a resource type this run produced nothing for), not an achievement, and reading it as
                // success would hide a source that has quietly stopped returning data.
                await LogActivityAsync(
                    destination, mappingProfile, context,
                    DestinationStageNames.Complete,
                    records.Count == 0 ? DestinationStageStatuses.NoData : DestinationStageStatuses.Succeeded,
                    records.Count, result.Count, ElapsedMs(startedAt),
                    detail: null, error: null, cancellationToken);
            }

            return result;
        }
        catch (Exception exception)
        {
            // Rethrown unchanged — this decorator observes, it never changes the pipeline's failure behaviour.
            _logger.LogError(
                LogEvents.DestinationWriteFailed,
                exception,
                "Failed writing {RecordCount} {ResourceType} record(s) to {DestinationType} destination " +
                "'{DestinationName}' after {ElapsedMs}ms: {FailureReason}",
                records.Count, mappingProfile.ResourceType, destination.DestinationType, destination.Name,
                ElapsedMs(startedAt), exception.Message);

            await LogActivityAsync(
                destination, mappingProfile, context,
                DestinationStageNames.Complete, DestinationStageStatuses.Failed,
                records.Count, writtenCount: 0, ElapsedMs(startedAt),
                detail: null, error: exception.Message, cancellationToken);

            throw;
        }
    }

    /// <summary>
    /// Writes one governance row, swallowing any failure.
    /// <para>Deliberately catches everything: a schema drift, a transient database outage or a full disk must not
    /// convert a successful destination write into a failure — and in the catch branch above, an exception raised
    /// here would <em>replace</em> the real one the caller needs to see. Nothing is rethrown, and nothing is
    /// re-logged through a second channel that could fail the same way. Same reasoning as
    /// <c>ApiRequestLoggingHandler</c>'s finally block.</para>
    /// </summary>
    private async Task LogActivityAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        PipelineWriteContext context,
        string stage,
        string status,
        int? recordCount,
        int? writtenCount,
        long durationMs,
        string? detail,
        string? error,
        CancellationToken cancellationToken)
    {
        if (_governanceLogger is null)
        {
            return;
        }

        try
        {
            await _governanceLogger.LogDestinationActivityAsync(
                new DestinationActivityEntry(
                    destination.Id,
                    destination.Name,
                    destination.DestinationType.ToString(),
                    stage,
                    status,
                    mappingProfile.ResourceType,
                    recordCount,
                    writtenCount,
                    durationMs,
                    detail,
                    error,
                    context.CorrelationId,
                    context.PipelineRunId == Guid.Empty ? null : context.PipelineRunId),
                cancellationToken);
        }
        catch
        {
            // Intentionally ignored — see the summary above.
        }
    }

    private static long ElapsedMs(long startedAt) =>
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
