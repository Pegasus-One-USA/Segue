using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.Webhook;

/// <summary>
/// The wire half of the <see cref="Domain.Enums.DestinationType.DataLakeWebhook"/> destination: resolves the
/// destination's credential, applies the configured auth scheme, optionally gzips and HMAC-signs the body, then
/// sends with bounded exponential-backoff retry.
///
/// Retry classification is the part worth reading twice. A lake front door answers a too-large batch with 413 and
/// a malformed body with 400 — retrying either just burns the budget and delays the run, so only transport faults,
/// timeouts, 429 and 5xx are retried. 429's <c>Retry-After</c> is honored when present, because an ingest endpoint
/// that publishes a throttle window means it.
/// </summary>
public sealed class DataLakeWebhookSender : IDataLakeWebhookSender
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFhirDestinationTokenProvider _tokenProvider;
    private readonly ILogger<DataLakeWebhookSender> _logger;

    public DataLakeWebhookSender(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider,
        ILogger<DataLakeWebhookSender> logger)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public async Task<DataLakeWebhookSendResult> SendAsync(
        DestinationConfiguration destination,
        DataLakeWebhookSettings settings,
        DataLakeWebhookBatch batch,
        CancellationToken cancellationToken)
    {
        // Auth mode "none" with a configured endpoint needs no secret at all; with a blank endpoint the secret IS
        // the endpoint (a pre-authorized Eventstream/Event Grid URL). Every other mode resolves credential material.
        var secret = settings.RequiresSecret || settings.EndpointComesFromSecret
            ? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)
            : null;

        var endpointUrl = settings.EndpointComesFromSecret ? secret : settings.EndpointUrl;
        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' resolved no endpoint URL — neither dest_dlwEndpointUrl/Target "
                    + "nor the stored secret contained one.");
        }

        if (settings.EndpointComesFromSecret)
        {
            // A secret-carried URL has never been through the request validator, so it gets the same https-only
            // check a metadata-configured one gets at parse time.
            DataLakeWebhookSettings.ValidateEndpointUrl(endpointUrl, destination.Name);
        }

        var bodyBytes = Encoding.UTF8.GetBytes(batch.Body);
        var payloadBytes = settings.Compression == DataLakeWebhookCompression.Gzip
            ? Gzip(bodyBytes)
            : bodyBytes;

        // Signed over the UNCOMPRESSED body: a verifier reconstructs the signature from the JSON it parsed, not
        // from whichever transfer encoding happened to be negotiated.
        var bearerToken = settings.AuthMode == DataLakeWebhookAuthMode.OAuth2ClientCredentials
            ? await AcquireOAuth2TokenAsync(settings, secret!, cancellationToken)
            : null;

        var httpClient = _httpClientFactory.CreateClient(nameof(DataLakeWebhookSender));
        var maxAttempts = settings.RetryCount + 1;
        int? lastStatusCode = null;
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;

            try
            {
                using var request = BuildRequest(
                    endpointUrl, settings, batch, payloadBytes, bodyBytes, secret, bearerToken, attempt);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

                using var response = await httpClient.SendAsync(request, timeout.Token);
                lastStatusCode = (int)response.StatusCode;

                if (IsAccepted(settings, response.StatusCode))
                {
                    return new DataLakeWebhookSendResult(true, lastStatusCode, attempt, null);
                }

                lastError = $"HTTP {lastStatusCode} {response.ReasonPhrase}".Trim();

                if (!IsRetryable(response.StatusCode))
                {
                    // A rejected-on-content response will be rejected identically next time — stop now and let the
                    // caller's failure mode decide, instead of spending the remaining attempts.
                    _logger.LogWarning(
                        "Data-lake webhook destination {DestinationId} got non-retryable {StatusCode} for batch {IdempotencyKey} ({RecordCount} records).",
                        destination.Id,
                        lastStatusCode,
                        batch.IdempotencyKey,
                        batch.RecordCount);
                    return new DataLakeWebhookSendResult(false, lastStatusCode, attempt, lastError);
                }

                retryAfter = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The run itself was cancelled — not a delivery failure to retry.
                throw;
            }
            catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
            {
                lastError = exception is OperationCanceledException
                    ? $"Timed out after {settings.TimeoutSeconds}s"
                    : exception.Message;
            }

            if (attempt < maxAttempts)
            {
                var backoff = retryAfter is { } wait && wait > TimeSpan.Zero
                    ? wait
                    : TimeSpan.FromSeconds(settings.RetryBackoffSeconds * Math.Pow(2, attempt - 1));

                await Task.Delay(backoff, cancellationToken);
            }
        }

        _logger.LogWarning(
            "Data-lake webhook destination {DestinationId} exhausted {Attempts} attempt(s) for batch {IdempotencyKey} ({RecordCount} records): {Error}",
            destination.Id,
            maxAttempts,
            batch.IdempotencyKey,
            batch.RecordCount,
            lastError);

        return new DataLakeWebhookSendResult(false, lastStatusCode, maxAttempts, lastError);
    }

    private HttpRequestMessage BuildRequest(
        string endpointUrl,
        DataLakeWebhookSettings settings,
        DataLakeWebhookBatch batch,
        byte[] payloadBytes,
        byte[] signedBytes,
        string? secret,
        string? bearerToken,
        int attempt)
    {
        var request = new HttpRequestMessage(new HttpMethod(settings.HttpMethod), endpointUrl)
        {
            Content = new ByteArrayContent(payloadBytes),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue(settings.ContentType) { CharSet = "utf-8" };
        if (settings.Compression == DataLakeWebhookCompression.Gzip)
        {
            request.Content.Headers.ContentEncoding.Add("gzip");
        }

        // Provenance headers a lake consumer can partition or de-duplicate on without parsing the body. The
        // idempotency key is identical across retries by construction (see DataLakeWebhookBatch).
        request.Headers.TryAddWithoutValidation("X-Idempotency-Key", batch.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-FHIRBridge-Resource-Type", batch.ResourceType);
        request.Headers.TryAddWithoutValidation("X-FHIRBridge-Destination-Object", batch.DestinationObject);
        request.Headers.TryAddWithoutValidation(
            "X-FHIRBridge-Record-Count", batch.RecordCount.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-FHIRBridge-Attempt", attempt.ToString(CultureInfo.InvariantCulture));

        // Configured static headers last, so a customer-supplied header can override a provenance default if their
        // collector needs a specific name for it.
        foreach (var header in settings.Headers)
        {
            request.Headers.Remove(header.Key);
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        ApplyAuth(request, settings, secret, bearerToken, signedBytes);

        return request;
    }

    private static void ApplyAuth(
        HttpRequestMessage request,
        DataLakeWebhookSettings settings,
        string? secret,
        string? bearerToken,
        byte[] signedBytes)
    {
        switch (settings.AuthMode)
        {
            case DataLakeWebhookAuthMode.None:
                break;

            case DataLakeWebhookAuthMode.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;

            case DataLakeWebhookAuthMode.OAuth2ClientCredentials:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                break;

            case DataLakeWebhookAuthMode.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation(settings.AuthHeaderName ?? "X-Api-Key", secret);
                break;

            case DataLakeWebhookAuthMode.Basic:
                // Secret is stored pre-formatted as "username:password" — the same convention
                // WebhookNotifierNodeExecutor's Basic mode already uses, so one documented shape covers both.
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret ?? string.Empty)));
                break;

            case DataLakeWebhookAuthMode.HmacSha256:
                ApplyHmac(request, settings, secret ?? string.Empty, signedBytes);
                break;

            default:
                throw new InvalidOperationException($"Unsupported data-lake webhook auth mode '{settings.AuthMode}'.");
        }
    }

    /// <summary>
    /// Signs "<c>{unix timestamp}.{body}</c>" rather than the body alone, and sends the timestamp in its own
    /// header. Without the timestamp in the signed material a captured request stays replayable forever; with it,
    /// a verifier can reject anything outside its own freshness window — the convention Stripe/GitHub-style
    /// receivers expect.
    /// </summary>
    private static void ApplyHmac(
        HttpRequestMessage request, DataLakeWebhookSettings settings, string secret, byte[] signedBytes)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

        var signingMaterial = new byte[Encoding.UTF8.GetByteCount(timestamp) + 1 + signedBytes.Length];
        var written = Encoding.UTF8.GetBytes(timestamp, signingMaterial);
        signingMaterial[written] = (byte)'.';
        signedBytes.CopyTo(signingMaterial, written + 1);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(signingMaterial));

        request.Headers.TryAddWithoutValidation(
            settings.SignatureHeaderName ?? "X-Signature-256", $"sha256={signature}");
        request.Headers.TryAddWithoutValidation(
            settings.TimestampHeaderName ?? "X-Signature-Timestamp", timestamp);
    }

    /// <summary>
    /// Reuses the already-registered destination token provider (Redis-backed cache in production), so a workflow
    /// writing hundreds of batches acquires one token rather than one per request. The stored secret is the client
    /// secret; client id/token endpoint/scope are non-secret metadata.
    /// </summary>
    private Task<string> AcquireOAuth2TokenAsync(
        DataLakeWebhookSettings settings, string clientSecret, CancellationToken cancellationToken)
        => _tokenProvider.GetAccessTokenAsync(
            new FhirDestinationOAuth2Options(
                settings.TokenEndpoint!, settings.ClientId!, clientSecret, settings.Scope),
            cancellationToken);

    private static bool IsAccepted(DataLakeWebhookSettings settings, HttpStatusCode statusCode)
        => settings.ExpectedStatusCodes.Count > 0
            ? settings.ExpectedStatusCodes.Contains((int)statusCode)
            : (int)statusCode is >= 200 and < 300;

    /// <summary>
    /// Only transport-level and capacity-level failures are worth another attempt. A 4xx other than 408/429 is the
    /// endpoint telling us this exact request is unacceptable — batch too large (413), body malformed (400),
    /// credential rejected (401/403) — none of which a retry changes.
    /// </summary>
    private static bool IsRetryable(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
            || (int)statusCode >= 500;

    private static byte[] Gzip(byte[] payload)
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return buffer.ToArray();
    }
}
