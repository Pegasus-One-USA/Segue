using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Infrastructure.Destinations.ApiEndpoint;

/// <summary>
/// Parsed, non-secret configuration for a <see cref="Domain.Enums.DestinationType.ApiEndpoint"/> destination.
/// Read from the same flat <c>dest_*</c>-keyed <see cref="DestinationConfiguration.ConnectionMetadataJson"/> bag
/// every other writer's wizard fields use — the pattern <see cref="Webhook.DataLakeWebhookSettings"/> established.
/// Credential material itself is never here: it lives only in <see cref="DestinationConfiguration.SecretReference"/>'s
/// Key Vault entry.
/// </summary>
public sealed record ApiEndpointSettings(
    string EndpointUrl,
    string HttpMethod,
    ApiEndpointAuthMode AuthMode,
    ApiEndpointPayloadShape PayloadShape,
    ApiEndpointCompression Compression,
    ApiEndpointFailureMode FailureMode,
    string ContentType,
    int BatchSize,
    int MaxRequestBytes,
    int TimeoutSeconds,
    int RetryCount,
    double RetryBackoffSeconds,
    IReadOnlyCollection<int> ExpectedStatusCodes,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> QueryParams,
    string? AuthHeaderName,
    string? ApiKeyQueryParamName,
    string? SignatureHeaderName,
    string? TimestampHeaderName,
    string? TokenEndpoint,
    string? ClientId,
    string? Scope,
    bool IncludeSourceJson,
    bool RequireHttps,
    string? BodyTemplateJson)
{
    /// <summary>True for every mode whose credential is resolved out of Key Vault.</summary>
    public bool RequiresSecret => AuthMode != ApiEndpointAuthMode.None;

    /// <summary>
    /// True when the caller supplied their own request-body shape instead of using the fixed
    /// <c>{ pipelineRunId, resourceType, ..., values: {...} }</c> envelope every other destination writer uses.
    /// Most partner/customer APIs expect a specific body shape that already has meaning to their own system (an
    /// order schema, a specific event contract) — hand-mapping every one of those fields onto new per-destination
    /// settings-table columns would be unworkable, so instead the caller pastes the exact JSON their API expects,
    /// with <c>{{fieldName}}</c> placeholders anywhere inside it. <see cref="MappedApiEndpointDestinationWriter"/>
    /// substitutes each placeholder with that record's mapped value for the same field name the Mapping Profile
    /// screen already assigns for every other destination (SQL columns, CSV headers, ...) — so no separate
    /// mapping UI is needed here.
    /// </summary>
    public bool HasBodyTemplate => !string.IsNullOrWhiteSpace(BodyTemplateJson);

    public const int DefaultBatchSize = 500;
    public const int DefaultMaxRequestBytes = 4 * 1024 * 1024;
    private const int MaxBatchSize = 50_000;
    private const int HardMaxRequestBytes = 96 * 1024 * 1024;

    public static ApiEndpointSettings Parse(DestinationConfiguration destination)
    {
        var json = destination.ConnectionMetadataJson;

        // Target is the endpoint for a row created through the "existing connection" path (which stamps Target),
        // dest_apiEndpointUrl for one created fresh by the wizard — the same dual read every other HTTP writer uses.
        var endpointUrl = FirstNonBlank(
            ConnectionMetadataReader.GetString(json, "dest_apiEndpointUrl"),
            destination.Target) ?? string.Empty;

        if (string.IsNullOrWhiteSpace(endpointUrl))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has no endpoint URL configured (Target or dest_apiEndpointUrl).");
        }

        // Off by default: an arbitrary customer API is not assumed to carry PHI the way DataLakeWebhook always
        // does, so plain http is allowed unless the caller opts into enforcing https (dest_apiRequireHttps).
        var requireHttps = ConnectionMetadataReader.GetBool(json, "dest_apiRequireHttps", false);
        ValidateEndpointUrl(endpointUrl, destination.Name, requireHttps);

        var httpMethod = (ConnectionMetadataReader.GetString(json, "dest_apiHttpMethod") ?? "POST")
            .Trim()
            .ToUpperInvariant();
        if (httpMethod is not ("POST" or "PUT" or "PATCH" or "DELETE"))
        {
            throw new InvalidOperationException(
                $"Destination '{destination.Name}' has unsupported HTTP method '{httpMethod}' — "
                    + "use POST, PUT, PATCH or DELETE.");
        }

        var authMode = ParseEnum(
            ConnectionMetadataReader.GetString(json, "dest_apiAuthMode"), ApiEndpointAuthMode.None);

        var payloadShape = ParseEnum(
            ConnectionMetadataReader.GetString(json, "dest_apiPayloadShape"), ApiEndpointPayloadShape.JsonArray);

        var contentType = FirstNonBlank(
            ConnectionMetadataReader.GetString(json, "dest_apiContentType"),
            payloadShape == ApiEndpointPayloadShape.Ndjson ? "application/x-ndjson" : "application/json")!;

        var authHeaderName = ConnectionMetadataReader.GetString(json, "dest_apiAuthHeaderName");
        if (authMode == ApiEndpointAuthMode.ApiKeyHeader && string.IsNullOrWhiteSpace(authHeaderName))
        {
            authHeaderName = "X-Api-Key";
        }

        var apiKeyQueryParamName = ConnectionMetadataReader.GetString(json, "dest_apiKeyQueryParamName");
        if (authMode == ApiEndpointAuthMode.ApiKeyQuery && string.IsNullOrWhiteSpace(apiKeyQueryParamName))
        {
            apiKeyQueryParamName = "api_key";
        }

        if (authMode == ApiEndpointAuthMode.OAuth2ClientCredentials)
        {
            RequireForAuthMode(json, "dest_apiTokenEndpoint", destination.Name, authMode);
            RequireForAuthMode(json, "dest_apiClientId", destination.Name, authMode);
        }

        var bodyTemplateJson = ConnectionMetadataReader.GetString(json, "dest_apiBodyTemplateJson");
        if (!string.IsNullOrWhiteSpace(bodyTemplateJson))
        {
            try
            {
                JsonNode.Parse(bodyTemplateJson);
            }
            catch (JsonException exception)
            {
                throw new InvalidOperationException(
                    $"Destination '{destination.Name}' has an invalid Request Body Template — it must be valid JSON.",
                    exception);
            }
        }

        return new ApiEndpointSettings(
            EndpointUrl: endpointUrl,
            HttpMethod: httpMethod,
            AuthMode: authMode,
            PayloadShape: payloadShape,
            Compression: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_apiCompression"), ApiEndpointCompression.None),
            FailureMode: ParseEnum(
                ConnectionMetadataReader.GetString(json, "dest_apiOnFailure"), ApiEndpointFailureMode.Fail),
            ContentType: contentType,
            BatchSize: Clamp(ConnectionMetadataReader.GetInt(json, "dest_apiBatchSize", DefaultBatchSize), 1, MaxBatchSize),
            MaxRequestBytes: Clamp(
                ConnectionMetadataReader.GetInt(json, "dest_apiMaxRequestBytes", DefaultMaxRequestBytes),
                1024,
                HardMaxRequestBytes),
            TimeoutSeconds: Clamp(ConnectionMetadataReader.GetInt(json, "dest_apiTimeoutSeconds", 30), 1, 600),
            RetryCount: Clamp(ConnectionMetadataReader.GetInt(json, "dest_apiRetryCount", 3), 0, 10),
            RetryBackoffSeconds: Clamp(ConnectionMetadataReader.GetInt(json, "dest_apiRetryBackoffSeconds", 2), 0, 60),
            ExpectedStatusCodes: ParseStatusCodes(ConnectionMetadataReader.GetString(json, "dest_apiExpectedStatusCodes")),
            Headers: ParseDictionary(ConnectionMetadataReader.GetString(json, "dest_apiHeadersJson")),
            QueryParams: ParseDictionary(ConnectionMetadataReader.GetString(json, "dest_apiQueryParamsJson")),
            AuthHeaderName: authHeaderName,
            ApiKeyQueryParamName: apiKeyQueryParamName,
            SignatureHeaderName: FirstNonBlank(
                ConnectionMetadataReader.GetString(json, "dest_apiSignatureHeaderName"), "X-Signature-256"),
            TimestampHeaderName: FirstNonBlank(
                ConnectionMetadataReader.GetString(json, "dest_apiTimestampHeaderName"), "X-Signature-Timestamp"),
            TokenEndpoint: ConnectionMetadataReader.GetString(json, "dest_apiTokenEndpoint"),
            ClientId: ConnectionMetadataReader.GetString(json, "dest_apiClientId"),
            Scope: ConnectionMetadataReader.GetString(json, "dest_apiScope"),
            IncludeSourceJson: ConnectionMetadataReader.GetBool(json, "dest_apiIncludeSourceJson", false),
            RequireHttps: requireHttps,
            BodyTemplateJson: bodyTemplateJson);
    }

    internal static void ValidateEndpointUrl(string endpointUrl, string destinationName, bool requireHttps)
    {
        if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' has an endpoint URL that is not an absolute URI: '{endpointUrl}'.");
        }

        if (!requireHttps || uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Destination '{destinationName}' must use https — 'Require HTTPS' is enabled for this API Endpoint "
                + $"destination, so plaintext '{uri.Scheme}' delivery is not allowed (loopback excepted).");
    }

    private static void RequireForAuthMode(
        string? json, string key, string destinationName, ApiEndpointAuthMode authMode)
    {
        if (string.IsNullOrWhiteSpace(ConnectionMetadataReader.GetString(json, key)))
        {
            throw new InvalidOperationException(
                $"Destination '{destinationName}' uses auth mode '{authMode}' but has no '{key}' configured.");
        }
    }

    /// <summary>Static headers or query params the endpoint requires. A malformed document yields an empty map
    /// rather than failing the destination — the same tolerance <see cref="ConnectionMetadataReader"/> applies to
    /// the surrounding bag.</summary>
    private static IReadOnlyDictionary<string, string> ParseDictionary(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Empty means "accept any 2xx" — not "accept nothing".</summary>
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
