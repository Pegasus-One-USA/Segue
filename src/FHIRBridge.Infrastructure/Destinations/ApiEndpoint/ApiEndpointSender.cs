using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Web;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Destinations.Auth;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <summary>
/// The wire half of the <see cref="Domain.Enums.DestinationType.ApiEndpoint"/> destination: resolves the
/// destination's credential, applies the configured auth scheme, optionally gzips and HMAC-signs the body, then
/// sends with bounded exponential-backoff retry.
///
/// Retry classification mirrors <see cref="Webhook.DataLakeWebhookSender"/>: only transport faults, timeouts, 429
/// and 5xx are retried — a 4xx other than 408/429 means the endpoint is telling us this exact request is
/// unacceptable, and retrying just burns the budget. 429's <c>Retry-After</c> is honored when present.
/// </summary>
public sealed class ApiEndpointSender : IApiEndpointSender, IDisposable
{
    private readonly ISecretProvider _secretProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IFhirDestinationTokenProvider _tokenProvider;
    private readonly ILogger<ApiEndpointSender> _logger;

    // Client-certificate (mTLS) handlers, cached per destination for the lifetime of this sender instance
    // rather than rebuilt on every batch — see GetOrCreateCertificateHttpClient. This class is registered
    // Scoped (one instance per pipeline-run scope), so the cache's lifetime is exactly "one run", and is
    // torn down in Dispose() below when that scope ends.
    private readonly Dictionary<Guid, HttpClient> _certificateClients = new();
    private readonly object _certificateClientsLock = new();

    // Ceiling on the exponential backoff between attempts. Unbounded, the doubling against the top of
    // RetryBackoffSeconds's/RetryCount's own allowed ranges (60s base, 10 retries) reaches roughly 17
    // hours of cumulative Task.Delay for a single batch — blocking the run and holding its scope (and
    // this sender's cached mTLS HttpClient, if any) open the whole time. 60s keeps every retry within a
    // sane wait while still giving Retry-After (checked separately, uncapped) priority when the server
    // states one explicitly.
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

    public ApiEndpointSender(
        ISecretProvider secretProvider,
        IHttpClientFactory httpClientFactory,
        IFhirDestinationTokenProvider tokenProvider,
        ILogger<ApiEndpointSender> logger)
    {
        _secretProvider = secretProvider;
        _httpClientFactory = httpClientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    public void Dispose()
    {
        lock (_certificateClientsLock)
        {
            foreach (var client in _certificateClients.Values)
            {
                client.Dispose();
            }
            _certificateClients.Clear();
        }
    }

    public async Task<ApiEndpointSendResult> SendAsync(
        DestinationConfiguration destination,
        ApiEndpointSettings settings,
        ApiEndpointBatch batch,
        CancellationToken cancellationToken)
    {
        var secret = settings.RequiresSecret
            ? await _secretProvider.GetSecretAsync(destination.SecretReference, cancellationToken)
            : null;

        var bodyBytes = Encoding.UTF8.GetBytes(batch.Body);
        var payloadBytes = settings.Compression == ApiEndpointCompression.Gzip ? Gzip(bodyBytes) : bodyBytes;

        // Signed over the UNCOMPRESSED body: a verifier reconstructs the signature from the JSON it parsed, not
        // from whichever transfer encoding happened to be negotiated.
        var bearerToken = settings.AuthMode == ApiEndpointAuthMode.OAuth2ClientCredentials
            ? await AcquireOAuth2TokenAsync(settings, secret!, cancellationToken)
            : null;

        // Client-certificate (mTLS) destinations need their own handler carrying that destination's specific
        // certificate, so they cannot come from the shared named HttpClientFactory pool every other auth mode
        // uses. Cached per destination for this sender's lifetime (see GetOrCreateCertificateHttpClient) rather
        // than rebuilt per batch — a multi-hundred-batch run building a fresh TLS client-auth handshake and
        // connection pool for every single batch would exhaust sockets/ephemeral ports under sustained volume.
        var httpClient = settings.AuthMode == ApiEndpointAuthMode.ClientCertificate
            ? GetOrCreateCertificateHttpClient(destination, secret ?? string.Empty)
            : _httpClientFactory.CreateClient(nameof(ApiEndpointSender));

        var requestUrl = BuildRequestUrl(settings, secret);
        var maxAttempts = settings.RetryCount + 1;
        int? lastStatusCode = null;
        string? lastError = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            TimeSpan? retryAfter = null;

            try
            {
                using var request = BuildRequest(
                    requestUrl, settings, batch, payloadBytes, bodyBytes, secret, bearerToken, attempt);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

                using var response = await httpClient.SendAsync(request, timeout.Token);
                lastStatusCode = (int)response.StatusCode;

                if (IsAccepted(settings, response.StatusCode))
                {
                    return new ApiEndpointSendResult(true, lastStatusCode, attempt, null);
                }

                lastError = $"HTTP {lastStatusCode} {response.ReasonPhrase}".Trim();

                if (!IsRetryable(response.StatusCode))
                {
                    _logger.LogWarning(
                        "API Endpoint destination {DestinationId} got non-retryable {StatusCode} for batch {IdempotencyKey} ({RecordCount} records).",
                        destination.Id,
                        lastStatusCode,
                        batch.IdempotencyKey,
                        batch.RecordCount);
                    return new ApiEndpointSendResult(false, lastStatusCode, attempt, lastError);
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
                    : TimeSpan.FromSeconds(
                        Math.Min(settings.RetryBackoffSeconds * Math.Pow(2, attempt - 1), MaxBackoff.TotalSeconds));

                await Task.Delay(backoff, cancellationToken);
            }
        }

        _logger.LogWarning(
            "API Endpoint destination {DestinationId} exhausted {Attempts} attempt(s) for batch {IdempotencyKey} ({RecordCount} records): {Error}",
            destination.Id,
            maxAttempts,
            batch.IdempotencyKey,
            batch.RecordCount,
            lastError);

        return new ApiEndpointSendResult(false, lastStatusCode, maxAttempts, lastError);
    }

    /// <summary>Appends configured static query params, plus the API key when auth mode is ApiKeyQuery.</summary>
    private static string BuildRequestUrl(ApiEndpointSettings settings, string? secret)
    {
        if (settings.QueryParams.Count == 0 && settings.AuthMode != ApiEndpointAuthMode.ApiKeyQuery)
        {
            return settings.EndpointUrl;
        }

        var query = HttpUtility.ParseQueryString(string.Empty);
        foreach (var param in settings.QueryParams)
        {
            query[param.Key] = param.Value;
        }

        if (settings.AuthMode == ApiEndpointAuthMode.ApiKeyQuery)
        {
            query[settings.ApiKeyQueryParamName ?? "api_key"] = secret ?? string.Empty;
        }

        // UriBuilder (not string concatenation on a raw '?' check) so an endpoint URL that already carries a
        // fragment (e.g. "https://api.example.com/ingest#v1") gets the query appended BEFORE the fragment —
        // a plain "does it contain '?'" check would land the params (including the ApiKeyQuery secret) inside
        // the fragment instead, which the URI parser strips before the request is ever sent, silently
        // authenticating with no API key on every batch.
        var builder = new UriBuilder(settings.EndpointUrl);
        var existingQuery = HttpUtility.ParseQueryString(builder.Query);
        foreach (string? key in existingQuery)
        {
            if (key is null)
            {
                // Valueless flags on the original URL (e.g. "?debug", "?debug&trace") are filed by
                // ParseQueryString under the null key, with each flag name as one of ITS values — not as
                // separate string keys — so they'd otherwise be silently dropped by the "key is not null"
                // check below every time this merge runs.
                foreach (var flag in existingQuery.GetValues(null) ?? [])
                {
                    query.Add(null, flag);
                }
                continue;
            }

            if (query[key] is null)
            {
                query[key] = existingQuery[key];
            }
        }
        builder.Query = query.ToString();
        return builder.Uri.ToString();
    }

    private HttpRequestMessage BuildRequest(
        string requestUrl,
        ApiEndpointSettings settings,
        ApiEndpointBatch batch,
        byte[] payloadBytes,
        byte[] signedBytes,
        string? secret,
        string? bearerToken,
        int attempt)
    {
        var request = new HttpRequestMessage(new HttpMethod(settings.HttpMethod), requestUrl)
        {
            Content = new ByteArrayContent(payloadBytes),
        };

        request.Content.Headers.ContentType = new MediaTypeHeaderValue(settings.ContentType) { CharSet = "utf-8" };
        if (settings.Compression == ApiEndpointCompression.Gzip)
        {
            request.Content.Headers.ContentEncoding.Add("gzip");
        }

        // Provenance headers a consumer can partition or de-duplicate on without parsing the body. The
        // idempotency key is identical across retries by construction (see ApiEndpointBatch).
        request.Headers.TryAddWithoutValidation("X-Idempotency-Key", batch.IdempotencyKey);
        request.Headers.TryAddWithoutValidation("X-FHIRBridge-Resource-Type", batch.ResourceType);
        request.Headers.TryAddWithoutValidation("X-FHIRBridge-Destination-Object", batch.DestinationObject);
        request.Headers.TryAddWithoutValidation(
            "X-FHIRBridge-Record-Count", batch.RecordCount.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-FHIRBridge-Attempt", attempt.ToString(CultureInfo.InvariantCulture));

        // Configured static headers last, so a customer-supplied header can override a provenance default if their
        // API needs a specific name for it.
        foreach (var header in settings.Headers)
        {
            request.Headers.Remove(header.Key);
            request.Content.Headers.Remove(header.Key);

            // .NET splits headers into two disjoint collections (request vs. content headers) and
            // HttpRequestHeaders.TryAddWithoutValidation silently returns false — no exception — for a name
            // that belongs to the content-headers collection instead (Content-Type, Content-Encoding,
            // Content-Length, ...). Without this fallback, a caller who configures dest_apiHeadersJson to
            // override Content-Type (e.g. adding a "+json" vendor suffix) would have it silently dropped,
            // with the request still going out under the auto-computed ContentType set above.
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        ApplyAuth(request, settings, secret, bearerToken, signedBytes);

        return request;
    }

    private static void ApplyAuth(
        HttpRequestMessage request,
        ApiEndpointSettings settings,
        string? secret,
        string? bearerToken,
        byte[] signedBytes)
    {
        switch (settings.AuthMode)
        {
            case ApiEndpointAuthMode.None:
            case ApiEndpointAuthMode.ApiKeyQuery:
            case ApiEndpointAuthMode.ClientCertificate:
                // ApiKeyQuery's credential is already in the URL (see BuildRequestUrl); ClientCertificate's is
                // presented at the TLS layer (see BuildClientCertificateHttpClient) — neither needs a header here.
                break;

            case ApiEndpointAuthMode.Bearer:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;

            case ApiEndpointAuthMode.OAuth2ClientCredentials:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
                break;

            case ApiEndpointAuthMode.ApiKeyHeader:
                request.Headers.TryAddWithoutValidation(settings.AuthHeaderName ?? "X-Api-Key", secret);
                break;

            case ApiEndpointAuthMode.Basic:
                // Secret is stored pre-formatted as "username:password" — the same convention used across every
                // other Basic-auth destination in this codebase.
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(secret ?? string.Empty)));
                break;

            case ApiEndpointAuthMode.HmacSha256:
                ApplyHmac(request, settings, secret ?? string.Empty, signedBytes);
                break;

            default:
                throw new InvalidOperationException($"Unsupported API Endpoint auth mode '{settings.AuthMode}'.");
        }
    }

    /// <summary>
    /// Signs "<c>{unix timestamp}.{body}</c>" rather than the body alone, and sends the timestamp in its own
    /// header, so a captured request cannot be replayed outside the verifier's freshness window.
    /// </summary>
    private static void ApplyHmac(
        HttpRequestMessage request, ApiEndpointSettings settings, string secret, byte[] signedBytes)
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
    /// writing hundreds of batches acquires one token rather than one per request.
    /// </summary>
    private Task<string> AcquireOAuth2TokenAsync(
        ApiEndpointSettings settings, string clientSecret, CancellationToken cancellationToken)
        => _tokenProvider.GetAccessTokenAsync(
            new FhirDestinationOAuth2Options(
                settings.TokenEndpoint!, settings.ClientId!, clientSecret, settings.Scope),
            cancellationToken);

    /// <summary>Returns this destination's cached client-certificate <see cref="HttpClient"/>, building and
    /// caching it on first use. See the field doc on <see cref="_certificateClients"/> for why this is cached
    /// rather than rebuilt per batch.</summary>
    private HttpClient GetOrCreateCertificateHttpClient(DestinationConfiguration destination, string secret)
    {
        lock (_certificateClientsLock)
        {
            if (_certificateClients.TryGetValue(destination.Id, out var existing))
            {
                return existing;
            }

            var client = BuildClientCertificateHttpClient(secret, destination.Name);
            _certificateClients[destination.Id] = client;
            return client;
        }
    }

    /// <summary>
    /// Builds a one-off <see cref="HttpClient"/> presenting this destination's client certificate. The secret is
    /// the base64-encoded PFX, optionally suffixed with <c>|&lt;password&gt;</c> when the PFX itself requires one.
    /// </summary>
    private static HttpClient BuildClientCertificateHttpClient(string secret, string destinationName)
    {
        var separatorIndex = secret.IndexOf('|');
        var pfxBase64 = separatorIndex >= 0 ? secret[..separatorIndex] : secret;
        var password = separatorIndex >= 0 ? secret[(separatorIndex + 1)..] : null;

        X509Certificate2 certificate;
        try
        {
            certificate = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(pfxBase64), password);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' uses auth mode 'clientCertificate' but its secret is not a "
                    + "valid base64-encoded PFX (optionally suffixed with '|<password>').",
                exception);
        }

        var handler = new HttpClientHandler
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
        };
        handler.ClientCertificates.Add(certificate);

        // HttpClientHandler.Dispose() does not dispose certificates added to ClientCertificates — it only holds a
        // reference, not ownership — so the certificate (private key material) would otherwise outlive this call
        // until finalized by the GC. This subclass disposes it alongside the handler/HttpClient.
        return new CertificateOwningHttpClient(handler, certificate)
        {
            // Same reasoning as the named HttpClientFactory client's own Timeout override above: only the
            // per-attempt linked CancellationTokenSource in SendAsync should govern how long a request runs.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    /// <summary>An <see cref="HttpClient"/> that also disposes the client certificate it was built with, once,
    /// when the client itself is disposed — see <see cref="BuildClientCertificateHttpClient"/>.</summary>
    private sealed class CertificateOwningHttpClient : HttpClient
    {
        private readonly X509Certificate2 _certificate;

        public CertificateOwningHttpClient(HttpMessageHandler handler, X509Certificate2 certificate)
            : base(handler, disposeHandler: true)
        {
            _certificate = certificate;
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                _certificate.Dispose();
            }
        }
    }

    private static bool IsAccepted(ApiEndpointSettings settings, HttpStatusCode statusCode)
        => settings.ExpectedStatusCodes.Count > 0
            ? settings.ExpectedStatusCodes.Contains((int)statusCode)
            : (int)statusCode is >= 200 and < 300;

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
