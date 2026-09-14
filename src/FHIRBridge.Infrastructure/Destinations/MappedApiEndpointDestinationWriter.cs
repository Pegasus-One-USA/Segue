using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.ApiEndpoint;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Writer for the general-purpose <c>ApiEndpoint</c> destination: mapped records are framed into batches and sent
/// to an arbitrary customer- or partner-owned HTTP API. See <see cref="Domain.Enums.DestinationType.ApiEndpoint"/>
/// for how this differs from <see cref="MappedRestApiDestinationWriter"/> (bare-bones, one record per request) and
/// <see cref="MappedDataLakeWebhookDestinationWriter"/> (purpose-built for data-lake ingestion front doors).
///
/// Batching is bounded twice over — by record count (<see cref="ApiEndpointSettings.BatchSize"/>) and by
/// serialized byte size (<see cref="ApiEndpointSettings.MaxRequestBytes"/>) — because most APIs cap request size,
/// and a record count safe for a 20-column Patient row can be far too large for an Observation batch.
/// </summary>
public sealed class MappedApiEndpointDestinationWriter : IConfiguredDestinationWriter
{
    private static readonly JsonSerializerOptions PayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IApiEndpointSender _sender;
    private readonly ILogger<MappedApiEndpointDestinationWriter> _logger;

    public MappedApiEndpointDestinationWriter(IApiEndpointSender sender, ILogger<MappedApiEndpointDestinationWriter> logger)
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

        var settings = ApiEndpointSettings.Parse(destination);
        var batches = BuildBatches(mappingProfile, records, context, settings);

        var written = 0;
        var writtenResourceIds = new List<string?>();
        var recordErrors = new List<string>();

        for (var index = 0; index < batches.Count; index++)
        {
            var (batchRecords, body) = batches[index];
            var batch = new ApiEndpointBatch(
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

            if (settings.FailureMode == ApiEndpointFailureMode.Fail)
            {
                throw new InvalidOperationException(
                    $"API Endpoint delivery to destination '{destination.Name}' failed. {reason}");
            }

            _logger.LogWarning(
                "API Endpoint destination {DestinationId} isolating failed batch {BatchIndex} of {BatchCount}: {Reason}",
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

    /// <summary>Stable across retries and across re-runs of the same batch, so an endpoint that honors it can
    /// de-duplicate a re-driven run instead of landing every record twice.</summary>
    private static string BuildIdempotencyKey(Guid pipelineRunId, MappingProfile mappingProfile, int batchIndex)
        => $"{pipelineRunId:N}:{mappingProfile.ResourceType}:{mappingProfile.DestinationObject}:{batchIndex}"
            .ToLowerInvariant();

    /// <summary>
    /// Splits the batch on whichever bound is hit first — record count or serialized bytes. A single record that
    /// alone exceeds the byte cap is still sent as its own batch: truncating or dropping it would lose data.
    /// </summary>
    private List<(List<MappedDestinationRecord> Records, string Body)> BuildBatches(
        MappingProfile mappingProfile,
        IReadOnlyCollection<MappedDestinationRecord> records,
        PipelineWriteContext context,
        ApiEndpointSettings settings)
    {
        // Parsed once per write, not once per record — SubstituteInPlace works on a deep clone per record so the
        // parsed tree is never mutated across records.
        var template = settings.HasBodyTemplate ? JsonNode.Parse(settings.BodyTemplateJson!) : null;

        // A template shaped like { "meta": {...}, "records": [ {...one record...} ] } — the same convention the
        // portal's "Load JSON payload" destination-side loader now generates — describes the WHOLE envelope, not
        // one record. Only meaningful when Payload shape is actually Envelope; for any other shape the template
        // is used exactly as written, same as always. Without this split, the one-record-shaped "records[0]"
        // item (including its own literal "meta"/"records" keys) would be substituted per record and THEN
        // wrapped in ANOTHER envelope by Frame/BuildEnvelope below — a whole extra {meta,records} nested inside
        // every record instead of one shared envelope around all of them.
        JsonNode? recordTemplate = template;
        JsonObject? metaTemplate = null;
        if (settings.PayloadShape == ApiEndpointPayloadShape.Envelope
            && template is JsonObject templateObj
            && templateObj["meta"] is JsonObject metaObj
            && templateObj["records"] is JsonArray { Count: 1 } recordsArray
            && recordsArray[0] is { } singleRecordTemplate)
        {
            metaTemplate = metaObj.DeepClone().AsObject();
            recordTemplate = singleRecordTemplate.DeepClone();
        }

        if (settings.PayloadShape == ApiEndpointPayloadShape.RecordPerRequest)
        {
            return records
                .Select(record => (
                    Records: new List<MappedDestinationRecord> { record },
                    Body: BuildRecordLine(record, settings, recordTemplate)))
                .ToList();
        }

        var batches = new List<(List<MappedDestinationRecord>, string)>();
        var current = new List<MappedDestinationRecord>();
        var currentLines = new List<string>();
        var currentBytes = 0;

        foreach (var record in records)
        {
            var line = BuildRecordLine(record, settings, recordTemplate);
            var lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

            var wouldExceedBytes = current.Count > 0 && currentBytes + lineBytes > settings.MaxRequestBytes;
            var wouldExceedCount = current.Count >= settings.BatchSize;

            if (wouldExceedBytes || wouldExceedCount)
            {
                batches.Add((current, Frame(currentLines, mappingProfile, context, settings, current, metaTemplate)));
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
            batches.Add((current, Frame(currentLines, mappingProfile, context, settings, current, metaTemplate)));
        }

        return batches;
    }

    /// <summary>NDJSON is joined with "\n" (not <see cref="Environment.NewLine"/>): a reader parsing
    /// line-delimited JSON expects LF regardless of which OS produced the file.</summary>
    private static string Frame(
        List<string> lines,
        MappingProfile mappingProfile,
        PipelineWriteContext context,
        ApiEndpointSettings settings,
        List<MappedDestinationRecord> batchRecords,
        JsonObject? metaTemplate)
        => settings.PayloadShape switch
        {
            ApiEndpointPayloadShape.Ndjson => string.Join('\n', lines),
            ApiEndpointPayloadShape.Envelope => BuildEnvelope(lines, mappingProfile, context, batchRecords, metaTemplate),
            _ => $"[{string.Join(',', lines)}]",
        };

    private static string BuildEnvelope(
        List<string> lines,
        MappingProfile mappingProfile,
        PipelineWriteContext context,
        List<MappedDestinationRecord> batchRecords,
        JsonObject? metaTemplate)
    {
        JsonObject meta;
        if (metaTemplate is not null && batchRecords.Count > 0)
        {
            // The caller's own meta shape — a "meta" field is mappable exactly like a "records" field is (the
            // portal's mapping canvas offers both as regular draggable columns, just distinctly labeled), so it
            // resolves the SAME way a per-record field does: against the batch's first record's mapped values
            // (record.Values, plus the same handful of addressable names — pipelineRunId/resourceType/... — every
            // per-record template already gets). "meta" is sent once per BATCH, not once per record, which is
            // exactly why this only ever reads ONE record rather than substituting per record like "records"
            // does — every record in a batch shares the same mapping profile/resource type, so its first record's
            // values are representative of the whole batch for whatever the caller chose to put in "meta".
            // recordCount is the one field with no per-record source at all (it's a fact about the whole batch,
            // not any single record) — addressable by name here same as the others, resolving to the real count.
            meta = metaTemplate.DeepClone().AsObject();
            var firstRecord = batchRecords[0];
            var recordCount = batchRecords.Count;
            SubstituteInPlace(meta, field =>
                field == "recordCount" ? recordCount : ResolveTemplateField(field, firstRecord));
        }
        else
        {
            // No custom meta template configured — the original fixed provenance envelope every caller got
            // before Request Body Template could describe the envelope itself. Never used once a caller opts
            // into a custom envelope shape (metaTemplate above), so this stays exactly as it always was for
            // every destination that hasn't touched this feature.
            meta = new JsonObject
            {
                ["resourceType"] = mappingProfile.ResourceType,
                ["destinationObject"] = mappingProfile.DestinationObject,
                ["recordCount"] = batchRecords.Count,
                ["routeName"] = context.RouteName,
                ["correlationId"] = context.CorrelationId,
                ["emittedOnUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            };
        }

        return $"{{\"meta\":{meta.ToJsonString()},\"records\":[{string.Join(',', lines)}]}}";
    }

    /// <summary>One record's serialized line — either the caller's own Request Body Template with its
    /// <c>{{fieldName}}</c> placeholders substituted, or (when no template is configured) the fixed
    /// pipelineRunId/resourceType/.../values envelope every other destination writer uses.</summary>
    private static string BuildRecordLine(
        MappedDestinationRecord record, ApiEndpointSettings settings, JsonNode? template)
    {
        if (template is not null)
        {
            var instance = template.DeepClone();
            SubstituteInPlace(instance, field => ResolveTemplateField(field, record));
            return instance.ToJsonString(PayloadJsonOptions);
        }

        return JsonSerializer.Serialize(BuildRecordPayload(record, settings), PayloadJsonOptions);
    }

    private static readonly Regex ExactPlaceholder = new(@"^\{\{\s*([A-Za-z0-9_.]+)\s*\}\}$", RegexOptions.Compiled);
    private static readonly Regex EmbeddedPlaceholder = new(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Walks the cloned template tree, replacing every string value that is or contains a <c>{{fieldName}}</c>
    /// placeholder. A string that is EXACTLY one placeholder (<c>"{{Age}}"</c>) is replaced with the field's own
    /// typed JSON value (a number stays a number, not <c>"42"</c>) — anything else (<c>"id-{{Age}}"</c>, or plain
    /// static text with no placeholder at all) is left as a string, with any placeholders inside it
    /// string-interpolated. Non-string template values (numbers, booleans, null) are copied through untouched —
    /// a template can mix fixed, static fields with mapped ones. Generic over `resolve` so the exact same walker
    /// serves both a per-record template (resolve = ResolveTemplateField against one record) and the "meta"
    /// section of a custom envelope (resolve = ResolveTemplateField against the batch's first record, plus
    /// recordCount — see BuildEnvelope).
    /// </summary>
    private static void SubstituteInPlace(JsonNode? node, Func<string, object?> resolve)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(kvp => kvp.Key).ToList())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        obj[key] = SubstituteString(text, resolve);
                    }
                    else
                    {
                        SubstituteInPlace(obj[key], resolve);
                    }
                }
                break;

            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    if (array[i] is JsonValue value && value.TryGetValue(out string? text))
                    {
                        array[i] = SubstituteString(text, resolve);
                    }
                    else
                    {
                        SubstituteInPlace(array[i], resolve);
                    }
                }
                break;
        }
    }

    private static JsonNode? SubstituteString(string text, Func<string, object?> resolve)
    {
        var exact = ExactPlaceholder.Match(text);
        if (exact.Success)
        {
            var resolved = resolve(exact.Groups[1].Value);
            return resolved is null ? null : JsonSerializer.SerializeToNode(resolved, resolved.GetType());
        }

        if (!EmbeddedPlaceholder.IsMatch(text))
        {
            // No placeholder at all — static template text, copied through verbatim.
            return JsonValue.Create(text);
        }

        var interpolated = EmbeddedPlaceholder.Replace(text, match =>
        {
            var resolved = resolve(match.Groups[1].Value);
            return resolved switch
            {
                null => string.Empty,
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => resolved.ToString() ?? string.Empty,
            };
        });
        return JsonValue.Create(interpolated);
    }

    /// <summary>A handful of record-envelope fields are addressable by name alongside the mapped values
    /// themselves, so a template can reference <c>{{sourceResourceId}}</c> without the mapping profile having to
    /// carry a redundant field for it.</summary>
    private static object? ResolveTemplateField(string field, MappedDestinationRecord record)
        => field switch
        {
            "pipelineRunId" => record.PipelineRunId,
            "resourceType" => record.ResourceType,
            "destinationObject" => record.DestinationObject,
            "sourceResourceId" => record.SourceResourceId,
            _ => record.Values.TryGetValue(field, out var value) ? value : null,
        };

    /// <summary>The per-record payload, plus the source resource when <c>dest_apiIncludeSourceJson</c> opts in.
    /// That flag defaults off deliberately: the mapped values have already been through the mapping profile and
    /// de-identification, while the raw source resource is the full FHIR document.</summary>
    private static Dictionary<string, object?> BuildRecordPayload(
        MappedDestinationRecord record, ApiEndpointSettings settings)
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
            // A source resource that isn't parseable JSON is carried through as text rather than failing the
            // batch — the record's mapped values are still valid and are what the destination primarily delivers.
            return JsonValue.Create(json);
        }
    }
}
