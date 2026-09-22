using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HealthAppBackend;

/// <summary>
/// Four sample third-party APIs, each expecting a different request-body shape, so FHIRBridge's <c>ApiEndpoint</c>
/// destination (see <c>ApiEndpointSettings</c>/<c>MappedApiEndpointDestinationWriter</c>) can be pointed at every
/// one of them and exercised end to end — single object, array of objects, an envelope wrapping an array, and an
/// arbitrary partner-shaped event body (the one meant for testing the destination's Request Body Template
/// feature). Every call, valid or not, lands in the single shared <see cref="ApiTestCallEntity"/> table — see its
/// own remarks for why one table rather than four.
///
/// All four receive endpoints are anonymous by design, same reasoning as
/// <see cref="DataLakeWebhookEndpoints"/>'s receiver: FHIRBridge calls them server-to-server with no session
/// cookie. The read-back/reset endpoints are anonymous here too (unlike the Data Lake Webhook ones) — this table
/// never holds anything but dummy validation-test payloads, so gating it behind login would only get in the way
/// of the thing it exists for: quickly checking, from Postman or a browser tab, what FHIRBridge actually sent.
/// </summary>
public static class ApiEndpointTestEndpoints
{
    public const string SingleRecord = "SingleRecord";
    public const string RecordsBatch = "RecordsBatch";
    public const string RecordsEnvelope = "RecordsEnvelope";
    public const string CustomEvent = "CustomEvent";

    /// <summary>Anything larger is rejected with 413 rather than buffered — same cap and reasoning as
    /// <see cref="DataLakeWebhookEndpoints"/>.</summary>
    private const int MaxBodyBytes = 8 * 1024 * 1024;

    public static void MapApiEndpointTestEndpoints(this WebApplication app)
    {
        // ── 1. Single object — "Create Patient Record" ─────────────────────────────────────────────────
        // Body: { "mrn": "...", "firstName": "...", "lastName": "...", "dateOfBirth": "YYYY-MM-DD", "gender": "..." }
        // Matches the ApiEndpoint destination's RecordPerRequest payload shape (or a batch size of 1).
        app.MapPost("/api/apitest/single-record", async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            if (!TryParseObject(body, out var root, out var parseError))
            {
                error = parseError;
            }
            else
            {
                error = RequireStringFields(root, "mrn", "firstName", "lastName");
            }

            var call = await RecordCallAsync(db, SingleRecord, body, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = SingleRecord })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 2. Array of objects — "Bulk Import Records" ────────────────────────────────────────────────
        // Body: [ { "mrn": "...", ... }, { "mrn": "...", ... }, ... ]
        // Matches the ApiEndpoint destination's JsonArray payload shape (its default).
        app.MapPost("/api/apitest/records-batch", async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
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
                            var fieldError = RequireStringFields(element, "mrn");
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

            var call = await RecordCallAsync(db, RecordsBatch, body, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = RecordsBatch, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 3. Envelope — "Batch With Run Metadata" ─────────────────────────────────────────────────────
        // Body: { "meta": { "resourceType": "...", "recordCount": N, ... }, "records": [ {...}, ... ] }
        // Matches the ApiEndpoint destination's Envelope payload shape.
        app.MapPost("/api/apitest/records-envelope", async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
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
            else if (!root.TryGetProperty("meta", out var meta) || meta.ValueKind != JsonValueKind.Object)
            {
                error = "Missing or invalid \"meta\" object.";
            }
            else if (RequireStringFields(meta, "resourceType") is { } metaError)
            {
                error = $"meta.{metaError}";
            }
            else if (!root.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            {
                error = "Missing or invalid \"records\" array.";
            }
            else if (records.GetArrayLength() == 0)
            {
                error = "\"records\" is an empty array — at least one record is required.";
            }
            else
            {
                recordCount = records.GetArrayLength();
            }

            var call = await RecordCallAsync(db, RecordsEnvelope, body, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = RecordsEnvelope, recordsReceived = recordCount })
                : Results.Json(new { status = "Rejected", callId = call.Id, error }, statusCode: 400);
        });

        // ── 4. Arbitrary partner-shaped event — "Custom Event" ─────────────────────────────────────────
        // Body: { "eventType": "...", "patient": { "id": "...", "fullName": "...", "age": N }, "source": "..." }
        // Not one of FHIRBridge's own framings at all — this is the API a real partner integration looks like,
        // for exercising the ApiEndpoint destination's Request Body Template feature (a caller-supplied JSON
        // shape with {{fieldName}} placeholders substituted per record) end to end.
        app.MapPost("/api/apitest/custom-event", async (HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            string? error = null;
            if (!TryParseObject(body, out var root, out var parseError))
            {
                error = parseError;
            }
            else if (RequireStringFields(root, "eventType") is { } eventError)
            {
                error = eventError;
            }
            else if (!root.TryGetProperty("patient", out var patient) || patient.ValueKind != JsonValueKind.Object)
            {
                error = "Missing or invalid \"patient\" object.";
            }
            else if (RequireStringFields(patient, "id") is { } patientError)
            {
                error = $"patient.{patientError}";
            }

            var call = await RecordCallAsync(db, CustomEvent, body, error, ct);
            return error is null
                ? Results.Ok(new { status = "Accepted", callId = call.Id, apiName = CustomEvent })
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

    private static async Task<ApiTestCallEntity> RecordCallAsync(
        HealthAppDbContext db, string apiName, string content, string? errorMessage, CancellationToken ct)
    {
        var call = new ApiTestCallEntity
        {
            ApiName = apiName,
            Content = content,
            IsValid = errorMessage is null,
            ErrorMessage = errorMessage,
            ReceivedOnUtc = DateTime.UtcNow,
        };
        db.ApiTestCalls.Add(call);
        await db.SaveChangesAsync(ct);
        return call;
    }

    /// <summary>Reads the whole body as text, decompressing first when the caller sent
    /// <c>Content-Encoding: gzip</c> — the ApiEndpoint destination's <c>dest_apiCompression</c> option — so gzip
    /// mode can actually be smoke-tested against this harness instead of receiving raw compressed bytes that fail
    /// every parse check below. <c>TooLarge</c> is true once <see cref="MaxBodyBytes"/> is exceeded (measured on
    /// the decompressed byte count, not the char count, which undercounts for any non-ASCII content), in which
    /// case <c>Body</c> is empty and nothing has been stored.</summary>
    private static async Task<(string Body, bool TooLarge)> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var isGzip = request.Headers.ContentEncoding
            .Any(value => string.Equals(value, "gzip", StringComparison.OrdinalIgnoreCase));
        Stream sourceStream = isGzip ? new GZipStream(request.Body, CompressionMode.Decompress) : request.Body;

        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await sourceStream.ReadAsync(chunk, ct)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxBodyBytes)
                {
                    return (string.Empty, true);
                }
            }

            return (Encoding.UTF8.GetString(buffer.ToArray()), false);
        }
        finally
        {
            if (isGzip)
            {
                await sourceStream.DisposeAsync();
            }
        }
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

    /// <summary>Returns null when every named field is present as a non-blank string, or a description of the
    /// first one that isn't.</summary>
    private static string? RequireStringFields(JsonElement element, params string[] fieldNames)
    {
        foreach (var name in fieldNames)
        {
            if (!element.TryGetProperty(name, out var value)
                || value.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(value.GetString()))
            {
                return $"\"{name}\" is required and must be a non-empty string.";
            }
        }

        return null;
    }
}
