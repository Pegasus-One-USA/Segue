using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
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
/// </summary>
public sealed class LoggingConfiguredDestinationWriter : IConfiguredDestinationWriter
{
    private readonly IConfiguredDestinationWriter _inner;
    private readonly ILogger _logger;

    public LoggingConfiguredDestinationWriter(IConfiguredDestinationWriter inner, ILogger logger)
    {
        _inner = inner;
        _logger = logger;
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

        try
        {
            var result = await _inner.WriteAsync(destination, mappingProfile, records, context, cancellationToken);

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
            }
            else
            {
                _logger.LogInformation(
                    LogEvents.DestinationWriteCompleted,
                    "Wrote {WrittenCount} of {RecordCount} {ResourceType} record(s) to {DestinationType} destination " +
                    "'{DestinationName}' in {ElapsedMs}ms.",
                    result.Count, records.Count, mappingProfile.ResourceType, destination.DestinationType,
                    destination.Name, ElapsedMs(startedAt));
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
            throw;
        }
    }

    private static long ElapsedMs(long startedAt) =>
        (long)System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
}
