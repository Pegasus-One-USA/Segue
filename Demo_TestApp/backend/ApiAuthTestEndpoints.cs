using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HealthAppBackend;

/// <summary>
/// One receive endpoint per auth mode FHIRBridge's <c>ApiEndpoint</c> destination itself supports (see
/// <c>ApiEndpointAuthMode</c> in <c>FHIRBridge.Infrastructure.Destinations.ApiEndpoint</c>), so every auth
/// configuration can be pointed at, run, and verified end to end — not just the four payload shapes
/// <see cref="ApiEndpointTestEndpoints"/> already covers.
///
/// Unlike <see cref="DataLakeWebhookEndpoints"/> (one active auth mode at a time, set via config, requiring a
/// restart to switch), every mode here is always live simultaneously with a fixed, openly-documented credential
/// (see <see cref="ApiTestAuthCredentials"/>) — a tester configures several destination rows, each against a
/// different one of these routes, in the same run without touching this app's configuration.
///
/// Every route accepts POST/PUT/PATCH/DELETE (see <see cref="ApiEndpointTestEndpoints.HttpMethods"/>) and, once
/// auth passes, stores whatever JSON body arrived — these endpoints exist to prove auth, not to re-validate
/// payload shape (that's what the four sample APIs are for). A body that isn't valid JSON is still recorded, just
/// marked invalid, exactly like <see cref="ApiEndpointTestEndpoints"/>'s own endpoints.
/// </summary>
public static class ApiAuthTestEndpoints
{
    public const string None = "None";
    public const string Bearer = "Bearer";
    public const string ApiKeyHeader = "ApiKeyHeader";
    public const string ApiKeyQuery = "ApiKeyQuery";
    public const string Basic = "Basic";
    public const string HmacSha256 = "HmacSha256";
    public const string OAuth2ClientCredentials = "OAuth2ClientCredentials";
    public const string ClientCertificate = "ClientCertificate";

    private const int MaxBodyBytes = 8 * 1024 * 1024;

    public static void MapApiAuthTestEndpoints(this WebApplication app)
    {
        MapAuthRoute(app, "none", None, request => null);

        MapAuthRoute(app, "bearer", Bearer, request =>
        {
            var header = request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                && FixedTimeEquals(header["Bearer ".Length..].Trim(), ApiTestAuthCredentials.BearerToken)
                    ? null
                    : $"Missing or invalid bearer token — expected Authorization: Bearer {ApiTestAuthCredentials.BearerToken}";
        });

        MapAuthRoute(app, "apikey-header", ApiKeyHeader, request =>
        {
            var presented = request.Headers.TryGetValue(ApiTestAuthCredentials.ApiKeyHeaderName, out var values)
                ? values.ToString()
                : null;
            return FixedTimeEquals(presented, ApiTestAuthCredentials.ApiKeyHeaderValue)
                ? null
                : $"Missing or invalid {ApiTestAuthCredentials.ApiKeyHeaderName} header.";
        });

        MapAuthRoute(app, "apikey-query", ApiKeyQuery, request =>
        {
            var presented = request.Query.TryGetValue(ApiTestAuthCredentials.ApiKeyQueryParamName, out var values)
                ? values.ToString()
                : null;
            return FixedTimeEquals(presented, ApiTestAuthCredentials.ApiKeyQueryValue)
                ? null
                : $"Missing or invalid ?{ApiTestAuthCredentials.ApiKeyQueryParamName}= query parameter.";
        });

        MapAuthRoute(app, "basic", Basic, request =>
        {
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

            return FixedTimeEquals(decoded, $"{ApiTestAuthCredentials.BasicUsername}:{ApiTestAuthCredentials.BasicPassword}")
                ? null
                : "Basic credentials do not match.";
        });

        // ── HMAC-SHA256 — needs the raw, exact bytes it was signed over, so this route reads the body itself
        // rather than going through the shared string-based path every other mode uses. ────────────────────────
        app.MapMethods("/api/apitest/auth/hmac", ApiEndpointTestEndpoints.HttpMethods, async (
            HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (bodyBytes, tooLarge) = await ReadBodyBytesAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            var body = Encoding.UTF8.GetString(bodyBytes);
            var authError = VerifyHmac(request, bodyBytes);

            var call = await ApiEndpointTestEndpoints.RecordCallAsync(
                db, $"Auth:{HmacSha256}", request.Method, body, HmacSha256,
                authError ?? DescribeBodyError(body), ct);

            if (authError is not null)
            {
                return Results.Json(new { status = "Rejected", callId = call.Id, error = authError }, statusCode: 401);
            }

            return Results.Ok(new { status = "Accepted", callId = call.Id, authMode = HmacSha256 });
        });

        // ── OAuth2 client-credentials token endpoint ────────────────────────────────────────────────────────
        // Matches exactly what FhirDestinationOAuth2TokenProvider (via ApiEndpointSender.AcquireOAuth2TokenAsync)
        // sends and expects: a form-encoded POST of grant_type/client_id/client_secret/scope, answered with
        // access_token + expires_in. Point the destination's Token Endpoint URL here.
        app.MapPost("/api/apitest/oauth/token", async (HttpRequest request, ApiTestOAuth2TokenStore tokens) =>
        {
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

            if (!FixedTimeEquals(form["client_id"], ApiTestAuthCredentials.OAuth2ClientId)
                || !FixedTimeEquals(form["client_secret"], ApiTestAuthCredentials.OAuth2ClientSecret))
            {
                return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            }

            var accessToken = tokens.Issue(TimeSpan.FromSeconds(ApiTestAuthCredentials.OAuth2TokenLifetimeSeconds));
            return Results.Ok(new
            {
                access_token = accessToken,
                token_type = "Bearer",
                expires_in = ApiTestAuthCredentials.OAuth2TokenLifetimeSeconds,
            });
        });

        MapAuthRouteWithServices<ApiTestOAuth2TokenStore>(app, "oauth2", OAuth2ClientCredentials, (request, tokens) =>
        {
            var header = request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                return "Missing or malformed Bearer Authorization header — acquire one from POST /api/apitest/oauth/token first.";
            }

            var presented = header["Bearer ".Length..].Trim();
            return tokens.IsValid(presented)
                ? null
                : "Access token is unknown or expired — it was not issued by /api/apitest/oauth/token, or has since expired.";
        });

        // ── mutual TLS (client certificate) ─────────────────────────────────────────────────────────────────
        // Requires this app's Kestrel HTTPS endpoint to have been configured with ClientCertificateMode.
        // AllowCertificate (see Program.cs) — otherwise HttpContext.Connection.ClientCertificate is always null
        // and every call here is rejected regardless of what the caller presented.
        app.MapMethods("/api/apitest/auth/client-certificate", ApiEndpointTestEndpoints.HttpMethods, async (
            HttpRequest request, HealthAppDbContext db, ApiTestClientCertificateStore certificates, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            var presented = request.HttpContext.Connection.ClientCertificate;
            string? authError = presented is null
                ? "No client certificate was presented during the TLS handshake."
                : !string.Equals(presented.Thumbprint, certificates.Thumbprint, StringComparison.OrdinalIgnoreCase)
                    ? "Client certificate thumbprint does not match this instance's test certificate — fetch the "
                        + "current one from GET /api/apitest/auth/client-certificate/credential (it changes every backend restart)."
                    : null;

            var call = await ApiEndpointTestEndpoints.RecordCallAsync(
                db, $"Auth:{ClientCertificate}", request.Method, body, ClientCertificate,
                authError ?? DescribeBodyError(body), ct);

            if (authError is not null)
            {
                return Results.Json(new { status = "Rejected", callId = call.Id, error = authError }, statusCode: 401);
            }

            return Results.Ok(new { status = "Accepted", callId = call.Id, authMode = ClientCertificate });
        });

        // Publishes the current run's self-signed test certificate — regenerated every restart (see
        // ApiTestClientCertificateStore), so the console/docs page reads it live rather than a value baked into
        // source. Anonymous, same reasoning as everything else here: nothing behind it is a real secret.
        app.MapGet("/api/apitest/auth/client-certificate/credential", (ApiTestClientCertificateStore certificates) =>
            Results.Ok(new
            {
                pfxBase64 = certificates.PfxBase64,
                password = ApiTestAuthCredentials.ClientCertificatePfxPassword,
                thumbprint = certificates.Thumbprint,
                formattedSecret = certificates.FormattedSecret,
            }));
    }

    // ──────────────────────────────────────────────────────────────────────────────────────────────────
    // shared route wiring

    /// <summary>Wires one auth mode's route for every allowed HTTP method: run <paramref name="checkAuth"/>,
    /// record the call (recording the auth failure OR, when auth passed, whether the body itself was valid
    /// JSON), and answer 401/200 accordingly.</summary>
    private static void MapAuthRoute(WebApplication app, string routeSegment, string authMode, Func<HttpRequest, string?> checkAuth)
        => app.MapMethods($"/api/apitest/auth/{routeSegment}", ApiEndpointTestEndpoints.HttpMethods, async (
            HttpRequest request, HealthAppDbContext db, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            var authError = checkAuth(request);
            var call = await ApiEndpointTestEndpoints.RecordCallAsync(
                db, $"Auth:{authMode}", request.Method, body, authMode, authError ?? DescribeBodyError(body), ct);

            if (authError is not null)
            {
                return Results.Json(new { status = "Rejected", callId = call.Id, error = authError }, statusCode: 401);
            }

            return Results.Ok(new { status = "Accepted", callId = call.Id, authMode });
        });

    /// <summary>Same as <see cref="MapAuthRoute"/>, but <paramref name="checkAuth"/> also needs a DI service
    /// (only OAuth2ClientCredentials, which validates against <see cref="ApiTestOAuth2TokenStore"/>).</summary>
    private static void MapAuthRouteWithServices<TService>(
        WebApplication app, string routeSegment, string authMode, Func<HttpRequest, TService, string?> checkAuth)
        where TService : notnull
        => app.MapMethods($"/api/apitest/auth/{routeSegment}", ApiEndpointTestEndpoints.HttpMethods, async (
            HttpRequest request, HealthAppDbContext db, TService service, CancellationToken ct) =>
        {
            var (body, tooLarge) = await ReadBodyAsync(request, ct);
            if (tooLarge)
            {
                return Results.Json(new { error = $"Body exceeds {MaxBodyBytes} bytes." }, statusCode: 413);
            }

            var authError = checkAuth(request, service);
            var call = await ApiEndpointTestEndpoints.RecordCallAsync(
                db, $"Auth:{authMode}", request.Method, body, authMode, authError ?? DescribeBodyError(body), ct);

            if (authError is not null)
            {
                return Results.Json(new { status = "Rejected", callId = call.Id, error = authError }, statusCode: 401);
            }

            return Results.Ok(new { status = "Accepted", callId = call.Id, authMode });
        });

    /// <summary>These routes only prove auth, not payload shape — an empty or non-JSON body is recorded as
    /// received but is not itself an auth failure. Returns null (valid) for an empty body too, since a bare
    /// auth-only probe (no body at all) is a legitimate way to exercise a mode.</summary>
    private static string? DescribeBodyError(string body)
    {
        if (body.Trim().Length == 0)
        {
            return null;
        }

        try
        {
            JsonDocument.Parse(body).Dispose();
            return null;
        }
        catch (JsonException ex)
        {
            return $"Body is not valid JSON: {ex.Message}";
        }
    }

    private static string? VerifyHmac(HttpRequest request, byte[] bodyBytes)
    {
        var timestamp = Header(request, ApiTestAuthCredentials.HmacTimestampHeaderName);
        var signature = Header(request, ApiTestAuthCredentials.HmacSignatureHeaderName);
        if (string.IsNullOrWhiteSpace(timestamp) || string.IsNullOrWhiteSpace(signature))
        {
            return $"Missing {ApiTestAuthCredentials.HmacSignatureHeaderName} / {ApiTestAuthCredentials.HmacTimestampHeaderName} header.";
        }

        if (!long.TryParse(timestamp, out var unixSeconds)
            || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSeconds) > 300)
        {
            return "Signature timestamp is missing, unparseable, or outside the accepted 5-minute window.";
        }

        var material = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + bodyBytes.Length];
        var written = Encoding.UTF8.GetBytes(timestamp, material);
        material[written] = (byte)'.';
        bodyBytes.CopyTo(material, written + 1);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(ApiTestAuthCredentials.HmacSharedSecret));
        var expected = "sha256=" + Convert.ToHexStringLower(hmac.ComputeHash(material));
        return FixedTimeEquals(signature.Trim(), expected) ? null : "Signature does not match.";
    }

    private static bool FixedTimeEquals(string? left, string right) =>
        !string.IsNullOrEmpty(left)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? Header(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) ? values.ToString() : null;

    private static async Task<(string Body, bool TooLarge)> ReadBodyAsync(HttpRequest request, CancellationToken ct)
    {
        var (bytes, tooLarge) = await ReadBodyBytesAsync(request, ct);
        return (Encoding.UTF8.GetString(bytes), tooLarge);
    }

    private static async Task<(byte[] Body, bool TooLarge)> ReadBodyBytesAsync(HttpRequest request, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                return (Array.Empty<byte>(), true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }
}
