using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace HealthAppBackend;

/// <summary>
/// A Data Lake Webhook receiver — the "lake" end of FHIRBridge's <c>DataLakeWebhook</c> destination, so the
/// whole pipeline can be exercised end to end against this demo app instead of a public capture service.
///
/// It accepts any JSON and lands it in SQL Server two ways at once (see <see cref="DataLakeBatchEntity"/>):
/// the entire document verbatim, and every property flattened into key/value rows. <c>StoreMode</c> in
/// configuration picks one or both.
///
/// It understands all four framings FHIRBridge can send (NDJSON, JSON array, envelope, one-object-per-request)
/// and gzip, but it does not require them — POST a bare object from Postman and it stores that too.
///
/// The receive endpoint is anonymous by design: FHIRBridge calls it server-to-server and has no session
/// cookie. Set <c>DataLakeWebhook:AuthMode</c> to bearer/apikey/hmac to require a credential, which is also
/// how you verify the destination's own auth modes actually work. The read-back endpoints keep the session
/// check every other route in this app uses.
/// </summary>
public static class DataLakeWebhookEndpoints
{
    private const string SessionCookieName = "hb_session";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Anything larger is rejected with 413 rather than buffered — the same signal a real managed
    /// ingest endpoint gives, and the one FHIRBridge's sender treats as non-retryable.</summary>
    private const int MaxBodyBytes = 32 * 1024 * 1024;

    public static void MapDataLakeWebhookEndpoints(this WebApplication app)
    {
        // ── receive ────────────────────────────────────────────────────────────────────────────────────
        app.MapPost("/api/datalake/webhook", async (
            HttpRequest request,
            HealthAppDbContext db,
            IConfiguration configuration,
            DataLakeTokenStore tokens,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("DataLakeWebhook");
            var settings = ReceiverSettings.Read(configuration);

            // Read the raw bytes before anything else: HMAC signs the exact body, so it has to be verified
            // against what actually arrived, not against a re-serialization of a parsed model.
            var rawBytes = await ReadBodyAsync(request, ct);
            if (rawBytes is null)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            var contentEncoding = request.Headers.ContentEncoding.ToString();
            var isGzip = contentEncoding.Contains("gzip", StringComparison.OrdinalIgnoreCase);
            var bodyBytes = isGzip ? Gunzip(rawBytes) : rawBytes;
            var body = Encoding.UTF8.GetString(bodyBytes);

            // Signature is computed over the UNCOMPRESSED body, matching what DataLakeWebhookSender signs.
            var authError = Authorize(request, settings, bodyBytes, tokens);
            if (authError is not null)
            {
                logger.LogWarning("Rejected a data-lake webhook: {Reason}", authError);
                return Results.Json(new { error = authError }, statusCode: 401);
            }

            var idempotencyKey = Header(request, "X-Idempotency-Key");

            // The point of the destination's derived (never random) idempotency key: a retry, or a whole
            // re-run of the same workflow, is recognised here instead of double-landing every record.
            if (!string.IsNullOrWhiteSpace(idempotencyKey))
            {
                var existing = await db.DataLakeBatches
                    .AsNoTracking()
                    .FirstOrDefaultAsync(b => b.IdempotencyKey == idempotencyKey, ct);

                if (existing is not null)
                {
                    logger.LogInformation(
                        "Duplicate data-lake batch {IdempotencyKey} — already stored as {BatchId}, not re-inserting.",
                        idempotencyKey, existing.BatchId);

                    // 200, not a 4xx: the sender did nothing wrong, and a non-2xx here would make FHIRBridge
                    // treat a successfully-de-duplicated delivery as a failure.
                    return Results.Ok(new
                    {
                        batchId = existing.BatchId,
                        duplicate = true,
                        recordsStored = existing.RecordsStored,
                        message = "Already received — recognised by idempotency key, nothing re-inserted.",
                    });
                }
            }

            if (body.Trim().Length == 0)
            {
                return Results.Json(new { error = "Body is empty." }, statusCode: 400);
            }

            var (shape, records) = ParsePayload(body);
            if (records.Count == 0)
            {
                return Results.Json(
                    new { error = $"No JSON records found in the body (parsed as {shape})." }, statusCode: 400);
            }

            var receivedOnUtc = DateTime.UtcNow;
            var batch = new DataLakeBatchEntity
            {
                BatchId = Guid.NewGuid(),
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey,
                ResourceType = Header(request, "X-FHIRBridge-Resource-Type"),
                DestinationObject = Header(request, "X-FHIRBridge-Destination-Object"),
                DeclaredRecordCount = HeaderInt(request, "X-FHIRBridge-Record-Count"),
                Attempt = HeaderInt(request, "X-FHIRBridge-Attempt"),
                PayloadShape = shape,
                ContentType = request.ContentType,
                ContentEncoding = isGzip ? "gzip" : null,
                AuthMode = settings.AuthMode,
                RawBody = settings.StoreRaw ? body : string.Empty,
                RawBodyBytes = bodyBytes.Length,
                RecordsStored = records.Count,
                ReceivedOnUtc = receivedOnUtc,
                RemoteIpAddress = request.HttpContext.Connection.RemoteIpAddress?.ToString(),
            };
            db.DataLakeBatches.Add(batch);

            for (var index = 0; index < records.Count; index++)
            {
                var element = records[index];
                var record = new DataLakeRecordEntity
                {
                    RecordId = Guid.NewGuid(),
                    BatchId = batch.BatchId,
                    SequenceInBatch = index,
                    PipelineRunId = ReadString(element, "pipelineRunId"),
                    ResourceType = ReadString(element, "resourceType") ?? batch.ResourceType,
                    DestinationObject = ReadString(element, "destinationObject") ?? batch.DestinationObject,
                    SourceResourceId = ReadString(element, "sourceResourceId"),
                    WrittenOnUtc = ReadDateTime(element, "writtenOnUtc"),
                    RawJson = settings.StoreRaw ? element.GetRawText() : string.Empty,
                    ReceivedOnUtc = receivedOnUtc,
                };
                db.DataLakeRecords.Add(record);

                if (!settings.StoreProperties)
                {
                    continue;
                }

                // FHIRBridge nests the mapped fields under "values"; flatten that when present so the
                // key/value rows are the mapped columns themselves rather than one row called "values".
                // Anything else (a hand-posted document) flattens from its root.
                var toFlatten = element.ValueKind == JsonValueKind.Object
                    && element.TryGetProperty("values", out var values)
                    && values.ValueKind == JsonValueKind.Object
                        ? values
                        : element;

                foreach (var (name, value, kind) in Flatten(toFlatten))
                {
                    db.DataLakeRecordValues.Add(new DataLakeRecordValueEntity
                    {
                        ValueId = Guid.NewGuid(),
                        RecordId = record.RecordId,
                        FieldName = name,
                        FieldValue = value,
                        ValueKind = kind,
                    });
                }
            }

            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Stored data-lake batch {BatchId}: {RecordCount} {ResourceType} record(s), shape {Shape}, attempt {Attempt}.",
                batch.BatchId, batch.RecordsStored, batch.ResourceType ?? "(unknown)", shape, batch.Attempt ?? 1);

            // 202 Accepted is what a landing endpoint should say, and it exercises the destination's
            // configurable accepted-status-codes setting (its default is "any 2xx", so this passes as-is).
            return Results.Accepted($"/api/datalake/batches/{batch.BatchId}", new
            {
                batchId = batch.BatchId,
                duplicate = false,
                payloadShape = shape,
                recordsStored = batch.RecordsStored,
                declaredRecordCount = batch.DeclaredRecordCount,
            });
        });

        // ── OAuth2 client-credentials token endpoint ───────────────────────────────────────────────────
        // Exists so the destination's OAuth2 mode can be tested without a real identity provider: a
        // shared-secret receiver cannot validate a rotating JWT, so for that mode this app issues the token
        // itself and then validates what comes back. Deliberately opaque random tokens, not JWTs — nothing
        // here inspects claims, so signing them would add ceremony without adding a check.
        //
        // Matches exactly what FhirDestinationOAuth2TokenProvider sends and expects: a form-encoded POST of
        // grant_type/client_id/client_secret/scope, answered with access_token + expires_in.
        app.MapPost("/api/datalake/token", async (
            HttpRequest request, DataLakeTokenStore tokens, IConfiguration configuration, ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("DataLakeWebhook");
            var oauth = configuration.GetSection("DataLakeWebhook:OAuth2");
            var expectedClientId = oauth["ClientId"];
            var expectedClientSecret = oauth["ClientSecret"];
            var lifetimeSeconds = int.TryParse(oauth["TokenLifetimeSeconds"], out var parsed) ? parsed : 300;

            if (string.IsNullOrWhiteSpace(expectedClientId) || string.IsNullOrWhiteSpace(expectedClientSecret))
            {
                return Results.Json(
                    new { error = "server_error", error_description = "DataLakeWebhook:OAuth2 ClientId/ClientSecret are not configured." },
                    statusCode: 500);
            }

            if (!request.HasFormContentType)
            {
                return Results.Json(
                    new { error = "invalid_request", error_description = "Expected application/x-www-form-urlencoded." },
                    statusCode: 400);
            }

            var form = await request.ReadFormAsync();
            if (form["grant_type"] != "client_credentials")
            {
                return Results.Json(
                    new { error = "unsupported_grant_type", error_description = "Only client_credentials is supported." },
                    statusCode: 400);
            }

            if (!FixedTimeEquals(form["client_id"], expectedClientId)
                || !FixedTimeEquals(form["client_secret"], expectedClientSecret))
            {
                logger.LogWarning("Rejected a data-lake token request: invalid client credentials.");
                // 401 + invalid_client is the OAuth2-conformant answer, and it's what makes FHIRBridge's
                // token provider throw before it ever builds the delivery request.
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            }

            var accessToken = tokens.Issue(TimeSpan.FromSeconds(lifetimeSeconds));
            logger.LogInformation(
                "Issued a data-lake access token (scope '{Scope}'), valid {Lifetime}s.",
                form["scope"].ToString(), lifetimeSeconds);

            return Results.Ok(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = lifetimeSeconds,
            });
        });

        // ── read back (session-authenticated, like the rest of this app) ───────────────────────────────
        app.MapGet("/api/datalake/batches", async (
            HttpContext http, SessionStore sessions, HealthAppDbContext db, int? take, CancellationToken ct) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var rows = await db.DataLakeBatches
                .AsNoTracking()
                .OrderByDescending(b => b.ReceivedOnUtc)
                .Take(Math.Clamp(take ?? 50, 1, 500))
                .Select(b => new
                {
                    b.BatchId,
                    b.IdempotencyKey,
                    b.ResourceType,
                    b.DestinationObject,
                    b.PayloadShape,
                    b.DeclaredRecordCount,
                    b.RecordsStored,
                    b.Attempt,
                    b.AuthMode,
                    b.ContentEncoding,
                    b.RawBodyBytes,
                    b.ReceivedOnUtc,
                })
                .ToListAsync(ct);

            return Results.Ok(rows);
        });

        app.MapGet("/api/datalake/batches/{batchId:guid}", async (
            Guid batchId, HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var batch = await db.DataLakeBatches.AsNoTracking().FirstOrDefaultAsync(b => b.BatchId == batchId, ct);
            if (batch is null)
            {
                return Results.NotFound();
            }

            var records = await db.DataLakeRecords
                .AsNoTracking()
                .Where(r => r.BatchId == batchId)
                .OrderBy(r => r.SequenceInBatch)
                .ToListAsync(ct);

            var recordIds = records.Select(r => r.RecordId).ToList();
            var values = await db.DataLakeRecordValues
                .AsNoTracking()
                .Where(v => recordIds.Contains(v.RecordId))
                .ToListAsync(ct);

            return Results.Ok(new
            {
                batch,
                records = records.Select(r => new
                {
                    r.RecordId,
                    r.SequenceInBatch,
                    r.PipelineRunId,
                    r.ResourceType,
                    r.DestinationObject,
                    r.SourceResourceId,
                    r.WrittenOnUtc,
                    r.RawJson,
                    values = values
                        .Where(v => v.RecordId == r.RecordId)
                        .OrderBy(v => v.FieldName)
                        .Select(v => new { v.FieldName, v.FieldValue, v.ValueKind }),
                }),
            });
        });

        // Clears everything this receiver has stored, so a demo run can be repeated from a clean slate —
        // including re-sending a batch whose idempotency key would otherwise be recognised as a duplicate.
        app.MapDelete("/api/datalake", async (
            HttpContext http, SessionStore sessions, HealthAppDbContext db, CancellationToken ct) =>
        {
            if (!TryGetSession(http, sessions))
            {
                return Results.Unauthorized();
            }

            var values = await db.DataLakeRecordValues.ExecuteDeleteAsync(ct);
            var records = await db.DataLakeRecords.ExecuteDeleteAsync(ct);
            var batches = await db.DataLakeBatches.ExecuteDeleteAsync(ct);

            return Results.Ok(new { batchesDeleted = batches, recordsDeleted = records, valuesDeleted = values });
        });
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    // configuration

    private sealed record ReceiverSettings(string AuthMode, string? Secret, string HeaderName, bool StoreRaw, bool StoreProperties)
    {
        public static ReceiverSettings Read(IConfiguration configuration)
        {
            var section = configuration.GetSection("DataLakeWebhook");
            var authMode = (section["AuthMode"] ?? "none").Trim().ToLowerInvariant();

            // Both by default: the raw document and the flattened properties are each useful, and storing
            // both is what makes the demo show what the destination actually sent. Set StoreMode to "raw" or
            // "properties" to land only one of them.
            var storeMode = (section["StoreMode"] ?? "both").Trim().ToLowerInvariant();

            return new ReceiverSettings(
                AuthMode: authMode,
                Secret: section["Secret"],
                HeaderName: section["HeaderName"] ?? "X-Api-Key",
                StoreRaw: storeMode is "both" or "raw",
                StoreProperties: storeMode is "both" or "properties");
        }
    }

    /// <summary>Returns null when the request is allowed, or the reason to report with a 401.</summary>
    private static string? Authorize(
        HttpRequest request, ReceiverSettings settings, byte[] signedBytes, DataLakeTokenStore tokens)
    {
        if (settings.AuthMode == "none")
        {
            return null;
        }

        // oauth2 is the exception: its credential is the issued token, checked against DataLakeTokenStore,
        // so DataLakeWebhook:Secret is irrelevant to it (DataLakeWebhook:OAuth2 configures it instead).
        if (settings.AuthMode != "oauth2" && string.IsNullOrWhiteSpace(settings.Secret))
        {
            return $"Receiver is configured for '{settings.AuthMode}' auth but DataLakeWebhook:Secret is not set.";
        }

        switch (settings.AuthMode)
        {
            case "bearer":
            {
                var header = request.Headers.Authorization.ToString();
                return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                    && FixedTimeEquals(header["Bearer ".Length..].Trim(), settings.Secret)
                        ? null
                        : "Missing or invalid bearer token.";
            }

            case "apikey":
                return FixedTimeEquals(Header(request, settings.HeaderName), settings.Secret)
                    ? null
                    : $"Missing or invalid {settings.HeaderName} header.";

            case "basic":
            {
                // The destination stores the credential pre-formatted as "username:password" and base64-encodes
                // it on the wire, so decoding here lets both sides hold the same readable value.
                var header = request.Headers.Authorization.ToString();
                if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    return "Missing or malformed Basic Authorization header.";
                }

                string decoded;
                try
                {
                    decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
                }
                catch (FormatException)
                {
                    return "Basic Authorization header is not valid base64.";
                }

                return FixedTimeEquals(decoded, settings.Secret)
                    ? null
                    : "Basic credentials do not match (expected DataLakeWebhook:Secret as \"username:password\").";
            }

            case "oauth2":
            {
                // Validates against tokens THIS app issued from /api/datalake/token — see that endpoint for
                // why the demo app plays identity provider rather than trying to verify a third-party JWT.
                var header = request.Headers.Authorization.ToString();
                if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    return "Missing or malformed Bearer Authorization header.";
                }

                var presented = header["Bearer ".Length..].Trim();
                return tokens.IsValid(presented)
                    ? null
                    : "Access token is unknown or expired — it was not issued by /api/datalake/token, or it has "
                        + "since expired (note FHIRBridge caches tokens, so a demo-app restart can leave it "
                        + "holding one this app no longer knows).";
            }

            case "hmac":
            {
                // Mirrors DataLakeWebhookSender.ApplyHmac exactly: HMAC-SHA256 over "{timestamp}.{body}",
                // with the timestamp carried in its own header and the digest sent as "sha256=<hex>".
                var timestamp = Header(request, "X-Signature-Timestamp");
                var signature = Header(request, "X-Signature-256");
                if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature))
                {
                    return "Missing X-Signature-256 / X-Signature-Timestamp header.";
                }

                // The timestamp is in the signed material precisely so a captured request stops being
                // replayable; rejecting anything outside a five-minute window is what makes that pay off.
                if (!long.TryParse(timestamp, out var unixSeconds)
                    || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > 300)
                {
                    return "Signature timestamp is missing, unparseable, or outside the accepted 5-minute window.";
                }

                var material = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + signedBytes.Length];
                var written = Encoding.UTF8.GetBytes(timestamp, material);
                material[written] = (byte)'.';
                signedBytes.CopyTo(material, written + 1);

                using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.Secret));
                var expected = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(material));
                return FixedTimeEquals(signature.Trim(), expected) ? null : "Signature does not match.";
            }

            default:
                return $"Unsupported DataLakeWebhook:AuthMode '{settings.AuthMode}'. "
                    + "Use none, bearer, apikey, basic, hmac or oauth2.";
        }
    }

    private static bool FixedTimeEquals(string? left, string right) =>
        !string.IsNullOrEmpty(left)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    // payload handling

    /// <summary>Reads the whole body, or null once it exceeds <see cref="MaxBodyBytes"/>.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static byte[] Gunzip(byte[] payload)
    {
        using var source = new MemoryStream(payload);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var target = new MemoryStream();
        gzip.CopyTo(target);
        return target.ToArray();
    }

    /// <summary>
    /// Detects the framing and returns the records inside it. Order of checks matters: an envelope is also a
    /// JSON object, so it has to be recognised before the single-object case. Each element is cloned because
    /// the <see cref="JsonDocument"/> it came from is disposed before the caller reads it.
    /// </summary>
    private static (string Shape, List<JsonElement> Records) ParsePayload(string body)
    {
        var trimmed = body.TrimStart();

        if (trimmed.StartsWith('['))
        {
            using var document = JsonDocument.Parse(body);
            return ("JsonArray", document.RootElement.EnumerateArray().Select(e => e.Clone()).ToList());
        }

        if (trimmed.StartsWith('{'))
        {
            // A single object OR an envelope. Only try it as one document first — a multi-line NDJSON body
            // also starts with "{" but is not valid JSON as a whole, so a parse failure falls through.
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
                {
                    return ("Envelope", records.EnumerateArray().Select(e => e.Clone()).ToList());
                }

                return ("SingleObject", [root.Clone()]);
            }
            catch (JsonException)
            {
                // Not one document — fall through to NDJSON.
            }
        }

        // NDJSON. Split on either line ending: FHIRBridge sends LF, but a hand-crafted file may use CRLF.
        // A line that doesn't parse is skipped rather than failing the batch, so one malformed line never
        // discards the good records around it.
        var lines = body.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var parsed = new List<JsonElement>(lines.Length);
        foreach (var line in lines)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                parsed.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                // Skipped — reflected in RecordsStored being lower than DeclaredRecordCount.
            }
        }

        return ("Ndjson", parsed);
    }

    /// <summary>
    /// Flattens a JSON value into (path, value, kind) triples. Objects join with ".", arrays with their index,
    /// so <c>name[0].family</c> becomes <c>name.0.family</c> — one predictable, queryable convention for both
    /// FHIRBridge's flat mapped values and an arbitrary nested document.
    /// </summary>
    private static IEnumerable<(string Name, string? Value, string Kind)> Flatten(JsonElement element, string prefix = "")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var name = prefix.Length == 0 ? property.Name : $"{prefix}.{property.Name}";
                    foreach (var flattened in Flatten(property.Value, name))
                    {
                        yield return flattened;
                    }
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var flattened in Flatten(item, $"{prefix}.{index}"))
                    {
                        yield return flattened;
                    }
                    index++;
                }
                break;

            case JsonValueKind.Null or JsonValueKind.Undefined:
                // Recorded, with a null value — "this field arrived and was empty" is different information
                // from "this field never arrived", and a lake consumer wants to tell them apart.
                yield return (prefix.Length == 0 ? "value" : prefix, null, "Null");
                break;

            default:
                yield return (
                    prefix.Length == 0 ? "value" : prefix,
                    element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText(),
                    element.ValueKind.ToString());
                break;
        }
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    // small helpers

    private static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;

    private static int? HeaderInt(HttpRequest request, string name) =>
        int.TryParse(Header(request, name), out var value) ? value : null;

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTime? ReadDateTime(JsonElement element, string propertyName) =>
        DateTime.TryParse(
            ReadString(element, propertyName),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;

    // Same shape as the other endpoint modules' own private copies (see Resource11Endpoints.TryGetSession).
    private static bool TryGetSession(HttpContext http, SessionStore sessions) =>
        http.Request.Cookies.TryGetValue(SessionCookieName, out var sessionId)
        && sessions.TryGet(sessionId, out _, out _, out _);
}

/// <summary>
/// Opaque access tokens issued by <c>POST /api/datalake/token</c> and validated by the webhook receiver's
/// <c>oauth2</c> mode. In-memory and singleton-scoped, mirroring this app's own SessionStore/EpicSessionStore:
/// a restart forgets every issued token, which is fine for a demo but is exactly the case the receiver's
/// error message calls out, since FHIRBridge caches its token independently.
/// </summary>
public sealed class DataLakeTokenStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _tokens = new(StringComparer.Ordinal);

    public string Issue(TimeSpan lifetime)
    {
        // Opaque and unguessable; nothing reads claims out of it, so there is nothing to encode.
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _tokens[token] = DateTimeOffset.UtcNow.Add(lifetime);
        Prune();
        return token;
    }

    public bool IsValid(string token)
        => _tokens.TryGetValue(token, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;

    /// <summary>Drops expired entries so a long-running demo doesn't accumulate them indefinitely.</summary>
    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in _tokens)
        {
            if (entry.Value <= now)
            {
                _tokens.TryRemove(entry.Key, out _);
            }
        }
    }
}
