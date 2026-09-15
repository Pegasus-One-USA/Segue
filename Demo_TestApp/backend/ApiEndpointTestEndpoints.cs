using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HealthAppBackend;

/// <summary>
/// Seven sample third-party APIs, each expecting a different request-body shape, so FHIRBridge's <c>ApiEndpoint</c>
/// destination (see <c>ApiEndpointSettings</c>/<c>MappedApiEndpointDestinationWriter</c>) can be pointed at every
/// one of them and exercised end to end — single object, array of objects, NDJSON, an envelope wrapping an array,
/// an arbitrary partner-shaped event body (for the Request Body Template feature), and the two multi-resource
/// combination modes (flat sibling arrays, nested parent-child — see <see cref="ApiEndpointMultiResourceMode"/> in
/// the main solution). Every call, valid or not, lands in the single shared <see cref="ApiTestCallEntity"/> table
/// — see its own remarks for why one table rather than one per API.
///
/// Every one of these validates the request body STRICTLY against that API's own expected shape (see
/// <see cref="ValidateShape"/>): every required field must be present with the right JSON type, every optional
/// field must have the right type when present, and — unlike a typical "just check the fields I care about"
/// sample — any field NOT part of the expected shape is itself rejected as an error, same as a real partner API
/// with a fixed contract would reject an unexpected field rather than silently ignore it.
///
/// All receive endpoints are anonymous by design, same reasoning as <see cref="DataLakeWebhookEndpoints"/>'s
/// receiver: FHIRBridge calls them server-to-server with no session cookie. The read-back/reset endpoints are
/// anonymous here too (unlike the Data Lake Webhook ones) — this table never holds anything but dummy
/// validation-test payloads, so gating it behind login would only get in the way of the thing it exists for:
/// quickly checking, from Postman or a browser tab, what FHIRBridge actually sent.
/// </summary>
public static class ApiEndpointTestEndpoints
{
    public const string SingleRecord = "SingleRecord";
    public const string RecordsBatch = "RecordsBatch";
    public const string RecordsNdjson = "RecordsNdjson";
    public const string RecordsEnvelope = "RecordsEnvelope";
    public const string CustomEvent = "CustomEvent";
    public const string MultiResourceFlat = "MultiResourceFlat";
    public const string MultiResourceNested = "MultiResourceNested";

    /// <summary>Anything larger is rejected with 413 rather than buffered — same cap and reasoning as
    /// <see cref="DataLakeWebhookEndpoints"/>.</summary>
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    /// <summary>Every method FHIRBridge's ApiEndpoint destination itself supports (see
    /// ApiEndpointSettings.Parse's HttpMethod validation) — no GET, since the destination only ever pushes data
    /// out, never reads it back.</summary>
    internal static readonly string[] HttpMethods = ["POST", "PUT", "PATCH", "DELETE"];

    // ── expected request-body shapes — one per sample API, enforced strictly by ValidateShape (a required
    // field missing or wrong-typed, an optional field wrong-typed when present, or ANY field not named here at
    // all, are all rejected) ────────────────────────────────────────────────────────────────────────────────
    private static readonly FieldSchema[] SingleRecordFields =
    [
        new("mrn", FieldKind.String),
        new("firstName", FieldKind.String),
        new("lastName", FieldKind.String),
        new("dateOfBirth", FieldKind.String, Required: false),
        new("gender", FieldKind.String, Required: false),
    ];

    /// <summary>One batch record — used for RecordsBatch's array elements, RecordsNdjson's lines, and
    /// RecordsEnvelope's "records" array elements: the same shape in every framing that carries plain records.</summary>
    private static readonly FieldSchema[] BatchRecordFields =
    [
        new("mrn", FieldKind.String),
        new("firstName", FieldKind.String, Required: false),
        new("lastName", FieldKind.String, Required: false),
    ];

    private static readonly FieldSchema[] EnvelopeMetaFields =
    [
        new("resourceType", FieldKind.String),
        new("recordCount", FieldKind.Number, Required: false),
        new("routeName", FieldKind.String, Required: false),
    ];

    private static readonly FieldSchema[] RecordsEnvelopeFields =
    [
        new("meta", FieldKind.Object, Children: EnvelopeMetaFields),
        new("records", FieldKind.Array, Children: BatchRecordFields),
    ];

    private static readonly FieldSchema[] CustomEventPatientFields =
    [
        new("id", FieldKind.String),
        new("fullName", FieldKind.String, Required: false),
        new("age", FieldKind.Number, Required: false),
    ];

    private static readonly FieldSchema[] CustomEventFields =
    [
        new("eventType", FieldKind.String),
        new("patient", FieldKind.Object, Children: CustomEventPatientFields),
        new("source", FieldKind.String, Required: false),
    ];

    /// <summary>Flat mode: the receiving API gets two sibling arrays with no nesting, so each encounter carries
    /// its own patientMrn to say who it belongs to — nothing about its position in the document implies that.</summary>
    private static readonly FieldSchema[] FlatEncounterFields =
    [
        new("encounterId", FieldKind.String),
        new("patientMrn", FieldKind.String),
        new("reason", FieldKind.String, Required: false),
    ];

    private static readonly FieldSchema[] MultiResourceFlatFields =
    [
        new("patients", FieldKind.Array, Children: BatchRecordFields),
        new("encounters", FieldKind.Array, Children: FlatEncounterFields),
    ];

    /// <summary>Nested mode: no patientMrn needed on the encounter — which patient it belongs to is exactly
    /// which patient object it's embedded inside.</summary>
    private static readonly FieldSchema[] NestedEncounterFields =
    [
        new("encounterId", FieldKind.String),
        new("reason", FieldKind.String, Required: false),
    ];

    private static readonly FieldSchema[] NestedPatientFields =
    [
        new("mrn", FieldKind.String),
        new("firstName", FieldKind.String, Required: false),
        new("lastName", FieldKind.String, Required: false),
        new("encounters", FieldKind.Array, Required: false, Children: NestedEncounterFields),
    ];

    private static readonly FieldSchema[] MultiResourceNestedFields =
    [
        new("patients", FieldKind.Array, Children: NestedPatientFields),
    ];

    public static void MapApiEndpointTestEndpoints(this WebApplication app)
    {
        // ── 1. Single object — "Create Patient Record" ─────────────────────────────────────────────────
        // Body: { "mrn": "...", "firstName": "...", "lastName": "...", "dateOfBirth": "YYYY-MM-DD", "gender": "..." }
        // Matches the ApiEndpoint destination's RecordPerRequest payload shape (or a batch size of 1).
        // Accepts POST/PUT/PATCH/DELETE — the same methods ApiEndpointSettings.Parse allows for the destination's
        // own "HTTP method" setting (see its validation there) — so a receiver here can be pointed at by any of
        // them; the method used has no bearing on validation, only on what's recorded against the call.
        app.MapMethods("/api/apitest/single-record", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = !TryParseObject(body, out var root, out var parseError)
                ? parseError
                : ValidateShape(root, SingleRecordFields);

            var call = await RecordCallAsync(db, SingleRecord, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = SingleRecord })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 2. Array of objects — "Bulk Import Records" ────────────────────────────────────────────────
        // Body: [ { "mrn": "...", ... }, { "mrn": "...", ... }, ... ]
        // Matches the ApiEndpoint destination's JsonArray payload shape (its default).
        app.MapMethods("/api/apitest/records-batch", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            var recordCount = 0;
            if (!TryParseDocument(body, out var document, out var parseError))
            {
                error = parseError;
            }
            else
            {
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Array)
                    {
                        error = "Body must be a JSON array of records.";
                    }
                    else if (root.GetArrayLength() == 0)
                    {
                        error = "Body is an empty array — at least one record is required.";
                    }
                    else
                    {
                        recordCount = root.GetArrayLength();
                        var index = 0;
                        foreach (var element in root.EnumerateArray())
                        {
                            var fieldError = ValidateShape(element, BatchRecordFields);
                            if (fieldError is not null)
                            {
                                error = $"Record {index}: {fieldError}";
                                break;
                            }

                            index++;
                        }
                    }
                }
            }

            var call = await RecordCallAsync(db, RecordsBatch, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = RecordsBatch, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 3. NDJSON — "Streamed Records" ──────────────────────────────────────────────────────────────
        // Body: one JSON object per line (no surrounding array/commas) — { "mrn": "...", ... }\n{ "mrn": "...", ... }
        // Matches the ApiEndpoint destination's NDJSON payload shape.
        app.MapMethods("/api/apitest/records-ndjson", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            var recordCount = 0;
            var lines = body.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Trim().Length > 0)
                .ToArray();

            if (lines.Length == 0)
            {
                error = "Body is empty — NDJSON requires at least one line, each a JSON object.";
            }
            else
            {
                for (var index = 0; index < lines.Length; index++)
                {
                    JsonDocument? lineDocument = null;
                    try
                    {
                        lineDocument = JsonDocument.Parse(lines[index]);
                    }
                    catch (JsonException ex)
                    {
                        error = $"Line {index}: not valid JSON: {ex.Message}";
                        break;
                    }

                    using (lineDocument)
                    {
                        var lineError = ValidateShape(lineDocument.RootElement, BatchRecordFields);
                        if (lineError is not null)
                        {
                            error = $"Line {index}: {lineError}";
                            break;
                        }
                    }

                    recordCount++;
                }
            }

            var call = await RecordCallAsync(db, RecordsNdjson, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = RecordsNdjson, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 4. Envelope — "Batch With Run Metadata" ─────────────────────────────────────────────────────
        // Body: { "meta": { "resourceType": "...", "recordCount": N, ... }, "records": [ {...}, ... ] }
        // Matches the ApiEndpoint destination's Envelope payload shape.
        app.MapMethods("/api/apitest/records-envelope", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            var recordCount = 0;
            if (!TryParseObject(body, out var root, out var parseError))
            {
                error = parseError;
            }
            else
            {
                error = ValidateShape(root, RecordsEnvelopeFields);
                if (error is null)
                {
                    recordCount = root.GetProperty("records").GetArrayLength();
                }
            }

            var call = await RecordCallAsync(db, RecordsEnvelope, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = RecordsEnvelope, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 5. Arbitrary partner-shaped event — "Custom Event" ─────────────────────────────────────────
        // Body: { "eventType": "...", "patient": { "id": "...", "fullName": "...", "age": N }, "source": "..." }
        // Not one of FHIRBridge's own framings at all — this is the API a real partner integration looks like,
        // for exercising the ApiEndpoint destination's Request Body Template feature (a caller-supplied JSON
        // shape with {{fieldName}} placeholders substituted per record) end to end.
        app.MapMethods("/api/apitest/custom-event", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = !TryParseObject(body, out var root, out var parseError)
                ? parseError
                : ValidateShape(root, CustomEventFields);

            var call = await RecordCallAsync(db, CustomEvent, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = CustomEvent })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 6. Multi-resource, flat — "Patients + Encounters (sibling arrays)" ─────────────────────────
        // Body: { "patients": [ {...} ], "encounters": [ { "encounterId", "patientMrn", ... } ] }
        // Matches the ApiEndpoint destination's multi-resource "Flat" mode (ApiEndpointMultiResourceMode.Flat) —
        // every participating resource type lands as its own top-level sibling array, correlated only by the
        // encounter's own patientMrn field, never by JSON nesting.
        app.MapMethods("/api/apitest/multi-resource-flat", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            var recordCount = 0;
            if (!TryParseObject(body, out var root, out var parseError))
            {
                error = parseError;
            }
            else
            {
                error = ValidateShape(root, MultiResourceFlatFields);
                if (error is null)
                {
                    recordCount = root.GetProperty("patients").GetArrayLength() + root.GetProperty("encounters").GetArrayLength();
                }
            }

            var call = await RecordCallAsync(db, MultiResourceFlat, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = MultiResourceFlat, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 7. Multi-resource, nested — "Patients With Nested Encounters" ──────────────────────────────
        // Body: { "patients": [ { "mrn", ..., "encounters": [ { "encounterId", ... } ] } ] }
        // Matches the ApiEndpoint destination's multi-resource "Nested" mode (ApiEndpointMultiResourceMode.Nested)
        // — each encounter is correlated to its parent patient and embedded directly inside that patient's own
        // record, so which patient an encounter belongs to is purely a function of where it sits in the document.
        app.MapMethods("/api/apitest/multi-resource-nested", HttpMethods, async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            var recordCount = 0;
            if (!TryParseObject(body, out var root, out var parseError))
            {
                error = parseError;
            }
            else
            {
                error = ValidateShape(root, MultiResourceNestedFields);
                if (error is null)
                {
                    var patients = root.GetProperty("patients");
                    recordCount = patients.GetArrayLength();
                    foreach (var patient in patients.EnumerateArray())
                    {
                        if (patient.TryGetProperty("encounters", out var encounters) && encounters.ValueKind == JsonValueKind.Array)
                        {
                            recordCount += encounters.GetArrayLength();
                        }
                    }
                }
            }

            var call = await RecordCallAsync(db, MultiResourceNested, request.Method, body, null, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = MultiResourceNested, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── read back / reset — anonymous, see this class's own remarks ────────────────────────────────
        app.MapGet("/api/apitest/calls", async (HealthAppDbContext db, int? take, string? apiName, CancellationToken ct) =>
        {
            var query = db.ApiTestCalls.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(apiName))
            {
                query = query.Where(c => c.ApiName == apiName);
            }

            var rows = await query
                .OrderByDescending(c => c.Id)
                .Take(Math.Clamp(take ?? 100, 1, 1000))
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        app.MapDelete("/api/apitest/calls", async (HealthAppDbContext db, CancellationToken ct) =>
        {
            var deleted = await db.ApiTestCalls.ExecuteDeleteAsync(ct);
            return Results.Ok(new { callsDeleted = deleted });
        });
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Shared with <see cref="ApiAuthTestEndpoints"/> — every call, on any route, lands in the one
    /// <see cref="ApiTestCallEntity"/> table via this same method.</summary>
    internal static async Task<ApiTestCallEntity> RecordCallAsync(
        HealthAppDbContext db,
        string apiName,
        string httpMethod,
        string content,
        string? authMode,
        string? errorMessage,
        CancellationToken ct)
    {
        var call = new ApiTestCallEntity
        {
            ApiName = apiName,
            HttpMethod = httpMethod,
            Content = content,
            AuthMode = authMode,
            IsValid = errorMessage is null,
            ErrorMessage = errorMessage,
            ReceivedOnUtc = DateTime.UtcNow,
        };
        db.ApiTestCalls.Add(call);
        await db.SaveChangesAsync(ct);
        return call;
    }

    /// <summary>Reads the whole body as text. <c>TooLarge</c> is true once <see cref="MaxBodyBytes"/> is
    /// exceeded, in which case <c>Body</c> is empty and nothing has been stored.</summary>
    private static async Task<(string Body, bool TooLarge)> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var reader = new StreamReader(request.Body);
        var buffer = new char[81920];
        var text = new System.Text.StringBuilder();
        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            text.Append(buffer, 0, read);
            if (text.Length > MaxBodyBytes)
            {
                return (string.Empty, true);
            }
        }

        return (text.ToString(), false);
    }

    private static bool TryParseDocument(string body, out JsonDocument document, out string? error)
    {
        if (body.Trim().Length == 0)
        {
            document = null!;
            error = "Body is empty.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(body);
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            document = null!;
            error = $"Body is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryParseObject(string body, out JsonElement root, out string? error)
    {
        if (!TryParseDocument(body, out var document, out error))
        {
            root = default;
            return false;
        }

        using (document)
        {
            root = document.RootElement.Clone();
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            error = "Body must be a JSON object.";
            return false;
        }

        return true;
    }

    private enum FieldKind { String, Number, Object, Array }

    /// <summary>One expected field in a sample API's request body. <paramref name="Children"/> is the nested
    /// schema for an Object field, or the per-element schema for an Array-of-objects field — null for a plain
    /// String/Number leaf.</summary>
    private sealed record FieldSchema(string Name, FieldKind Kind, bool Required = true, FieldSchema[]? Children = null);

    /// <summary>
    /// Strictly validates a JSON object against an expected shape: every <see cref="FieldSchema.Required"/> field
    /// must be present with the right JSON type, every optional field must have the right type when present,
    /// Object/Array children recurse into their own schema — and any property NOT named in <paramref name="schema"/>
    /// is itself rejected, same as a real partner API with a fixed contract would reject a field it doesn't
    /// recognize rather than silently ignore it. Returns null when the element matches exactly, or a description
    /// of the first mismatch (dotted/indexed path built up through <paramref name="pathPrefix"/> as it recurses).
    /// </summary>
    private static string? ValidateShape(JsonElement element, FieldSchema[] schema, string pathPrefix = "")
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return $"\"{(pathPrefix.Length > 0 ? pathPrefix.TrimEnd('.') : "(root)")}\" must be a JSON object.";
        }

        foreach (var field in schema)
        {
            var path = $"{pathPrefix}{field.Name}";
            if (!element.TryGetProperty(field.Name, out var value))
            {
                if (field.Required)
                {
                    return $"\"{path}\" is required.";
                }

                continue;
            }

            var kindError = field.Kind switch
            {
                FieldKind.String => value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())
                    ? "must be a non-empty string." : null,
                FieldKind.Number => value.ValueKind != JsonValueKind.Number ? "must be a number." : null,
                FieldKind.Object => value.ValueKind != JsonValueKind.Object ? "must be a JSON object." : null,
                FieldKind.Array => value.ValueKind != JsonValueKind.Array ? "must be a JSON array." : null,
                _ => null,
            };
            if (kindError is not null)
            {
                return $"\"{path}\" {kindError}";
            }

            if (field.Kind == FieldKind.Object && field.Children is not null)
            {
                var nested = ValidateShape(value, field.Children, $"{path}.");
                if (nested is not null)
                {
                    return nested;
                }
            }
            else if (field.Kind == FieldKind.Array && field.Children is not null)
            {
                if (value.GetArrayLength() == 0)
                {
                    return $"\"{path}\" must be a non-empty array.";
                }

                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    var itemError = ValidateShape(item, field.Children, $"{path}[{index}].");
                    if (itemError is not null)
                    {
                        return itemError;
                    }

                    index++;
                }
            }
        }

        var expectedNames = schema.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (!expectedNames.Contains(property.Name))
            {
                return $"\"{pathPrefix}{property.Name}\" is not an expected field for this API — remove it or check "
                    + "for a typo.";
            }
        }

        return null;
    }
}
