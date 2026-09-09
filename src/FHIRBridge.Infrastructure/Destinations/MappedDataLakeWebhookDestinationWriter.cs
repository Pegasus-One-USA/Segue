using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Webhook;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writer for the <c>DataLakeWebhook</c> destination: mapped records are framed into batches and POSTed to a
/// data-lake ingestion endpoint (Fabric Eventstream custom endpoint, Databricks/Snowpipe Streaming REST, an
/// API-Gateway front door over S3, Splunk HEC, Event Grid).
///
/// This is the DATA plane, and that distinction is the whole reason it exists separately from two things it
/// resembles:
/// <list type="bullet">
/// <item><description><see cref="MappedRestApiDestinationWriter"/> sends one record per request with no auth beyond
/// the raw secret, no batching, no retry and no idempotency — fine for a low-volume callback, unusable as a lake
/// feed where a single bulk export is tens of thousands of records.</description></item>
/// <item><description>The Runtime plane's <c>WebhookNotifierNode</c> is the CONTROL plane: it consumes a previous
/// destination's write RESULT and pings a URL with a summary, explicitly refusing record-level data to keep PHI
/// out of notification channels. It is a companion to this writer, not a substitute — a graph can land records
/// here and then notify an orchestrator with that node.</description></item>
/// </list>
///
/// Batching is bounded twice over — by record count (<see cref="DataLakeWebhookSettings.BatchSize"/>) and by
/// serialized byte size (<see cref="DataLakeWebhookSettings.MaxRequestBytes"/>) — because every managed ingest
/// endpoint caps request size, and a record count that is safe for a 20-column Patient row is far too large for an
/// Observation batch carrying full source JSON.
/// </summary>
public sealed class MappedDataLakeWebhookDestinationWriter : IConfiguredDestinationWriter
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IDataLakeWebhookSender _sender;
    private readonly ILogger<MappedDataLakeWebhookDestinationWriter> _logger;

    public MappedDataLakeWebhookDestinationWriter(
        IDataLakeWebhookSender sender, ILogger<MappedDataLakeWebhookDestinationWriter> logger)
    {
        _sender = sender;
        _logger = logger;
    }

    public async Task<DestinationWriteResult> WriteAsync(
        DestinationConfiguration destination,
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return new DestinationWriteResult(0);
        }

        var settings = DataLakeWebhookSettings.Parse(destination);
        var batches = BuildBatches(mappingProfile, records, context, settings);

        var written = 0;
        var writtenResourceIds = new List<string?>();
        var recordErrors = new List<string>();

        for (var index = 0; index < batches.Count; index++)
        {
            var (batchRecords, body) = batches[index];
            var batch = new DataLakeWebhookBatch(
                body,
                batchRecords.Count,
                BuildIdempotencyKey(batchRecords[0].PipelineRunId, mappingProfile, index),
                mappingProfile.ResourceType,
                mappingProfile.DestinationObject);

            var result = await _sender.SendAsync(destination, settings, batch, cancellationToken);

            if (result.Delivered)
            {
                written += batchRecords.Count;
                writtenResourceIds.AddRange(batchRecords.Select(record => record.SourceResourceId));
                continue;
            }

            var reason = $"Batch {index + 1}/{batches.Count} ({batchRecords.Count} record(s)) not delivered after "
                + $"{result.Attempts} attempt(s): {result.Error ?? "unknown error"}";

            if (settings.FailureMode == DataLakeWebhookFailureMode.Fail)
            {
                // Default: a dropped clinical batch is data loss, so it fails the node and the run rather than
                // being reported as a partial success nobody looks at.
                throw new InvalidOperationException(
                    $"Data-lake webhook delivery to destination '{destination.Name}' failed. {reason}");
            }

            // IsolateBatch: report through the existing RecordErrors channel, which the destination node executor
            // already aggregates into WorkflowRunStatus.PartialSuccess and execution history.
            _logger.LogWarning(
                "Data-lake webhook destination {DestinationId} isolating failed batch {BatchIndex} of {BatchCount}: {Reason}",
                destination.Id,
                index + 1,
                batches.Count,
                reason);
            recordErrors.Add(reason);
        }

        return new DestinationWriteResult(
            written,
            RecordErrors: recordErrors.Count > 0 ? recordErrors : null,
            WrittenResourceIds: writtenResourceIds);
    }

    /// <summary>
    /// Stable across retries and across re-runs of the same batch: pipeline run + destination object + batch index.
    /// A lake front door that honors an idempotency key can then de-duplicate a re-driven run instead of landing
    /// every record twice. Using a fresh Guid here — the obvious-looking choice — would silently defeat that.
    /// </summary>
    private static string BuildIdempotencyKey(Guid pipelineRunId, MappingProfile mappingProfile, int batchIndex)
        => $"{pipelineRunId:N}:{mappingProfile.ResourceType}:{mappingProfile.DestinationObject}:{batchIndex}"
            .ToLowerInvariant();

    /// <summary>
    /// Splits the batch on whichever bound is hit first — record count or serialized bytes. A single record that
    /// alone exceeds the byte cap is still sent as its own batch: truncating or dropping it would lose data, and
    /// the endpoint's own 413 (non-retryable, see the sender) reports the real problem accurately.
    /// </summary>
    private List<(List<MappedDestinationRecord> Records, string Body)> BuildBatches(
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        DataLakeWebhookSettings settings)
    {
        if (settings.PayloadShape == DataLakeWebhookPayloadShape.RecordPerRequest)
        {
            return records
                .Select(record => (
                    Records: new List<MappedDestinationRecord> { record },
                    Body: JsonSerializer.Serialize(BuildRecordPayload(record, settings), PayloadJsonOptions)))
                .ToList();
        }

        var batches = new List<(List<MappedDestinationRecord>, string)>();
        var current = new List<MappedDestinationRecord>();
        var currentLines = new List<string>();
        var currentBytes = 0;

        foreach (var record in records)
        {
            var line = JsonSerializer.Serialize(BuildRecordPayload(record, settings), PayloadJsonOptions);
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

            var wouldExceedBytes = current.Count > 0 && currentBytes + lineBytes > settings.MaxRequestBytes;
            var wouldExceedCount = current.Count >= settings.BatchSize;

            if (wouldExceedBytes || wouldExceedCount)
            {
                batches.Add((current, Frame(currentLines, mappingProfile, context, settings, current.Count)));
                current = [];
                currentLines = [];
                currentBytes = 0;
            }

            current.Add(record);
            currentLines.Add(line);
            currentBytes += lineBytes;
        }

        if (current.Count > 0)
        {
            batches.Add((current, Frame(currentLines, mappingProfile, context, settings, current.Count)));
        }

        return batches;
    }

    /// <summary>
    /// Wraps the batch's already-serialized record lines in the configured framing. NDJSON is joined with "\n"
    /// (not <see cref="Environment.NewLine"/>): a lake reader parsing line-delimited JSON expects LF regardless of
    /// which OS produced the file, and CRLF is what makes a Windows-produced NDJSON feed fail on Spark.
    /// </summary>
    private static string Frame(
        List<string> lines,
        MappingProfile mappingProfile,
        PipelineWriteContext context,
        DataLakeWebhookSettings settings,
        int recordCount)
        => settings.PayloadShape switch
        {
            DataLakeWebhookPayloadShape.Ndjson => string.Join('\n', lines),
            DataLakeWebhookPayloadShape.JsonArray => $"[{string.Join(',', lines)}]",
            DataLakeWebhookPayloadShape.Envelope => BuildEnvelope(lines, mappingProfile, context, recordCount),
            _ => string.Join('\n', lines),
        };

    private static string BuildEnvelope(
        List<string> lines, MappingProfile mappingProfile, PipelineWriteContext context, int recordCount)
    {
        var meta = new JsonObject
        {
            ["resourceType"] = mappingProfile.ResourceType,
            ["destinationObject"] = mappingProfile.DestinationObject,
            ["recordCount"] = recordCount,
            ["routeName"] = context.RouteName,
            ["correlationId"] = context.CorrelationId,
            ["emittedOnUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

        return $"{{\"meta\":{meta.ToJsonString()},\"records\":[{string.Join(',', lines)}]}}";
    }

    /// <summary>
    /// The per-record payload. <see cref="MappedDestinationSerialization.ToPayload"/>'s shape is kept verbatim so a
    /// consumer already reading the REST API destination's records needs no change, plus the source resource when
    /// <c>dest_dlwIncludeSourceJson</c> opts in. That flag defaults off deliberately: the mapped values have been
    /// through the destination's mapping profile and its de-identification profile, while the raw source resource
    /// is the full FHIR document — far more PHI than most lake consumers need or are cleared for.
    /// </summary>
    private static Dictionary<string, object?> BuildRecordPayload(
        MappedDestinationRecord record, DataLakeWebhookSettings settings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["pipelineRunId"] = record.PipelineRunId,
            ["resourceType"] = record.ResourceType,
            ["destinationObject"] = record.DestinationObject,
            ["sourceResourceId"] = record.SourceResourceId,
            ["writtenOnUtc"] = DateTime.UtcNow,
            ["values"] = record.Values,
        };

        if (settings.IncludeSourceJson && record.SourceJson is { Length: > 0 } sourceJson)
        {
            // Parsed, not embedded as a string, so the lake lands a real nested object instead of a JSON blob a
            // consumer has to double-decode.
            payload["resource"] = TryParseJson(sourceJson);
        }

        return payload;
    }

    private static JsonNode? TryParseJson(string json)
    {
        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            // A source resource that isn't parseable JSON is carried through as text rather than failing the batch —
            // the record's mapped values are still valid and are what the destination is primarily delivering.
            return JsonValue.Create(json);
        }
    }
}
