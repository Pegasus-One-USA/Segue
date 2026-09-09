using System.Text.Json;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.Webhook;

/// <summary>
/// Parsed, non-secret configuration for a <see cref="Domain.Enums.DestinationType.DataLakeWebhook"/> destination.
/// Read from the same flat <c>dest_*</c>-keyed <see cref="DestinationConfiguration.ConnectionMetadataJson"/> bag
/// every other writer's wizard fields use, so no schema migration is needed to add this destination type — exactly
/// the pattern <see cref="Blob.BlobDestinationSettings"/> established. Credential material itself is never here:
/// it lives only in <see cref="DestinationConfiguration.SecretReference"/>'s Key Vault entry.
/// </summary>
public sealed record DataLakeWebhookSettings(
    string EndpointUrl,
    DataLakeWebhookAuthMode AuthMode,
    DataLakeWebhookPayloadShape PayloadShape,
    DataLakeWebhookCompression Compression,
    DataLakeWebhookFailureMode FailureMode,
    string HttpMethod,
    string ContentType,
    int BatchSize,
    int MaxRequestBytes,
    int TimeoutSeconds,
    int RetryCount,
    double RetryBackoffSeconds,
    IReadOnlyCollection<int> ExpectedStatusCodes,
    IReadOnlyDictionary<string, string> Headers,
    string? AuthHeaderName,
    string? SignatureHeaderName,
    string? TimestampHeaderName,
    string? TokenEndpoint,
    string? ClientId,
    string? Scope,
    bool IncludeSourceJson)
{
    /// <summary>
    /// True for every mode whose credential is resolved out of Key Vault. <see cref="DataLakeWebhookAuthMode.None"/>
    /// is the exception twice over: the endpoint URL may itself BE the secret (see
    /// <see cref="EndpointComesFromSecret"/>), and when <c>dest_dlwEndpointUrl</c> is populated there is no
    /// credential to fetch at all.
    /// </summary>
    public bool RequiresSecret => AuthMode != DataLakeWebhookAuthMode.None;

    /// <summary>
    /// True when the endpoint URL was not configured in metadata, so the resolved secret is the full ingest URL
    /// rather than a credential — the shape a Fabric Eventstream / Event Grid pre-authorized endpoint takes, and
    /// the same "secret holds the target" convention <c>MappedRestApiDestinationWriter</c> already uses.
    /// </summary>
    public bool EndpointComesFromSecret => string.IsNullOrWhiteSpace(EndpointUrl);

    public const int DefaultBatchSize = 500;
    public const int DefaultMaxRequestBytes = 4 * 1024 * 1024;
    private const int MaxBatchSize = 50_000;
    private const int HardMaxRequestBytes = 96 * 1024 * 1024;

    public static DataLakeWebhookSettings Parse(DestinationConfiguration destination)
    {
        var json = destination.ConnectionMetadataJson;

        // Target is the endpoint for a row created through the "existing connection" path (which stamps Target),
        // dest_dlwEndpointUrl for one created fresh by the wizard — the same dual read BlobDestinationSettings
        // does for its container name. Both blank is legal: the secret then carries the URL.
        var endpointUrl = FirstNonBlank(
            ConnectionMetadataReader.GetString(json, "dest_dlwEndpointUrl"),
            destination.Target) ?? string.Empty;

        var authMode = ParseEnum(
            ConnectionMetadataReader.GetString(json, "dest_dlwAuthMode"), DataLakeWebhookAuthMode.None);

        if (!string.IsNullOrWhiteSpace(endpointUrl))
        {
            ValidateEndpointUrl(endpointUrl, destination.Name);
        }
        else if (authMode != DataLakeWebhookAuthMode.None)
        {
            // Only the URL-is-the-credential mode may omit the endpoint. Any other mode with a blank endpoint means
            // the secret holds a credential AND there is nowhere to send it — fail here rather than at first write.
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has no endpoint URL configured (Target or dest_dlwEndpointUrl). "
                    + "Only auth mode 'none' may omit it, in which case the stored secret must be the full ingest URL.");
        }

        var payloadShape = ParseEnum(
            ConnectionMetadataReader.GetString(json, "dest_dlwPayloadShape"), DataLakeWebhookPayloadShape.Ndjson);

        var httpMethod = (ConnectionMetadataReader.GetString(json, "dest_dlwHttpMethod") ?? "POST")
            .Trim()
            .ToUpperInvariant();
        if (httpMethod is not ("POST" or "PUT" or "PATCH"))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has unsupported HTTP method '{httpMethod}' — use POST, PUT or PATCH.");
        }

        // Content type follows the payload shape unless explicitly overridden: an endpoint that wants NDJSON under
        // a vendor content type (Splunk HEC's "application/json" for line-delimited events) can say so.
        var contentType = FirstNonBlank(
            ConnectionMetadataReader.GetString(json, "dest_dlwContentType"),
            payloadShape == DataLakeWebhookPayloadShape.Ndjson ? "application/x-ndjson" : "application/json")!;

        var authHeaderName = ConnectionMetadataReader.GetString(json, "dest_dlwAuthHeaderName");
        if (authMode == DataLakeWebhookAuthMode.ApiKeyHeader && string.IsNullOrWhiteSpace(authHeaderName))
        {
            authHeaderName = "X-Api-Key";
        }

        if (authMode == DataLakeWebhookAuthMode.OAuth2ClientCredentials)
        {
            RequireForAuthMode(json, "dest_dlwTokenEndpoint", destination.Name, authMode);
            RequireForAuthMode(json, "dest_dlwClientId", destination.Name, authMode);
        }

        return new DataLakeWebhookSettings(
            EndpointUrl: endpointUrl,
            AuthMode: authMode,
            PayloadShape: payloadShape,
            Compression: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_dlwCompression"), DataLakeWebhookCompression.None),
            FailureMode: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_dlwOnFailure"), DataLakeWebhookFailureMode.Fail),
            HttpMethod: httpMethod,
            ContentType: contentType,
            BatchSize: Clamp(ConnectionMetadataReader.GetInt(json, "dest_dlwBatchSize", DefaultBatchSize), 1, MaxBatchSize),
            MaxRequestBytes: Clamp(
                ConnectionMetadataReader.GetInt(json, "dest_dlwMaxRequestBytes", DefaultMaxRequestBytes),
                1024,
                HardMaxRequestBytes),
            TimeoutSeconds: Clamp(ConnectionMetadataReader.GetInt(json, "dest_dlwTimeoutSeconds", 30), 1, 600),
            RetryCount: Clamp(ConnectionMetadataReader.GetInt(json, "dest_dlwRetryCount", 3), 0, 10),
            RetryBackoffSeconds: Clamp(ConnectionMetadataReader.GetInt(json, "dest_dlwRetryBackoffSeconds", 2), 0, 60),
            ExpectedStatusCodes: ParseStatusCodes(ConnectionMetadataReader.GetString(json, "dest_dlwExpectedStatusCodes")),
            Headers: ParseHeaders(ConnectionMetadataReader.GetString(json, "dest_dlwHeadersJson")),
            AuthHeaderName: authHeaderName,
            SignatureHeaderName: FirstNonBlank(
                ConnectionMetadataReader.GetString(json, "dest_dlwSignatureHeaderName"), "X-Signature-256"),
            TimestampHeaderName: FirstNonBlank(
                ConnectionMetadataReader.GetString(json, "dest_dlwTimestampHeaderName"), "X-Signature-Timestamp"),
            TokenEndpoint: ConnectionMetadataReader.GetString(json, "dest_dlwTokenEndpoint"),
            ClientId: ConnectionMetadataReader.GetString(json, "dest_dlwClientId"),
            Scope: ConnectionMetadataReader.GetString(json, "dest_dlwScope"),
            IncludeSourceJson: ConnectionMetadataReader.GetBool(json, "dest_dlwIncludeSourceJson", false));
    }

    /// <summary>
    /// Unlike the Runtime plane's notifier node — whose payload is a write summary with no patient data in it —
    /// this destination puts mapped record values (and optionally the whole source resource) in the request body,
    /// so it is a PHI egress path and plaintext HTTP is refused outright rather than warned about. Loopback is
    /// exempted so a developer can point it at a local collector without a certificate.
    /// </summary>
    internal static void ValidateEndpointUrl(string endpointUrl, string destinationName)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' has an endpoint URL that is not an absolute URI: '{endpointUrl}'.");
        }

        if (uri.Scheme == Uri.UriSchemeHttps)
        {
            return;
        }

        if (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Destination '{destinationName}' must use https — a data-lake webhook carries mapped record data, "
                + $"so plaintext '{uri.Scheme}' delivery is not allowed (loopback excepted for local development).");
    }

    private static void RequireForAuthMode(
        string? json, string key, string destinationName, DataLakeWebhookAuthMode authMode)
    {
        if (string.IsNullOrWhiteSpace(ConnectionMetadataReader.GetString(json, key)))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' uses auth mode '{authMode}' but has no '{key}' configured.");
        }
    }

    /// <summary>
    /// Static headers the endpoint requires (a Databricks warehouse id, an HEC channel GUID, a tenant routing key).
    /// A malformed document yields no headers rather than failing the destination — the same tolerance
    /// <see cref="ConnectionMetadataReader"/> applies to the surrounding bag.
    /// </summary>
    private static IReadOnlyDictionary<string, string> ParseHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(headersJson)
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Empty means "accept any 2xx" (see the sender's success check) — not "accept nothing".</summary>
    private static IReadOnlyCollection<int> ParseStatusCodes(string? raw)
        => (raw ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(code => int.TryParse(code, out var parsed) ? parsed : (int?)null)
            .Where(code => code is not null)
            .Select(code => code!.Value)
            .Distinct()
            .ToArray();

    private static TEnum ParseEnum<TEnum>(string? raw, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) ? parsed : fallback;

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);

    private static string? FirstNonBlank(params string?[] candidates)
        => candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
}
