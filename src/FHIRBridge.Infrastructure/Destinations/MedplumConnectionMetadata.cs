using System.Text.Json;
using System.Text.Json.Serialization;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// The non-secret Medplum connection fields carried on <see cref="Domain.Entities.DestinationConfiguration.ConnectionMetadataJson"/>.
/// Kept parallel to the other writers' <c>dest_*</c> metadata bags. The client secret is NEVER stored here — it lives
/// only in the destination's <see cref="Domain.ValueObjects.SecretReference"/> (Key Vault). The FHIR base URL comes
/// from the destination's <c>Target</c>; everything else needed to mint a token / choose the upsert key is here.
/// </summary>
public sealed record MedplumConnectionMetadata
{
    /// <summary>OAuth2 <c>client_id</c> (the Medplum <c>ClientApplication</c> UUID). Required for the token request.</summary>
    [JsonPropertyName("medplumClientId")]
    public string? ClientId { get; init; }

    /// <summary>
    /// OAuth2 token endpoint. When omitted it is derived from the FHIR base URL by replacing the trailing
    /// <c>/fhir/R4</c> segment with <c>/oauth2/token</c> (e.g. <c>https://api.medplum.com/fhir/R4</c> →
    /// <c>https://api.medplum.com/oauth2/token</c>).
    /// </summary>
    [JsonPropertyName("medplumTokenUrl")]
    public string? TokenUrl { get; init; }

    /// <summary>
    /// The FHIR <c>identifier.system</c> to upsert against (conditional <c>PUT {type}?identifier={system}|{value}</c>).
    /// When set, the writer prefers the resource's identifier whose system matches this over the first identifier /
    /// logical-id fallback. Typically the source system's key namespace, e.g. <c>http://epic.example/patientId</c>.
    /// </summary>
    [JsonPropertyName("medplumIdentifierSystem")]
    public string? IdentifierSystem { get; init; }

    /// <summary>
    /// Which token-endpoint credential to use: <c>"client_secret"</c> (default) or <c>"private_key_jwt"</c>. In both
    /// cases the destination's <see cref="Domain.ValueObjects.SecretReference"/> holds the material — the symmetric
    /// secret for the former, the PEM-encoded private key for the latter.
    /// </summary>
    [JsonPropertyName("medplumAuthMethod")]
    public string? AuthMethod { get; init; }

    /// <summary>
    /// The <c>kid</c> to stamp into the JWT header for the <c>private_key_jwt</c> flow, matching the key id published
    /// in the <c>ClientApplication.jwksUri</c>. Ignored for the client-secret flow.
    /// </summary>
    [JsonPropertyName("medplumKeyId")]
    public string? KeyId { get; init; }

    /// <summary>True when this destination is configured for the SMART Backend Services private-key JWT flow.</summary>
    public bool UsesPrivateKeyJwt =>
        string.Equals(AuthMethod, "private_key_jwt", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Write strategy: <c>"per_record"</c> (default — one conditional PUT per record) or <c>"async_batch"</c>
    /// (chunk records into async batch Bundles of conditional PUTs). The async batch path is
    /// <b>quota-exempt</b> in Medplum — the interaction-weight budget (writes cost 100 points each) is not charged for
    /// work done inside a <c>Prefer: respond-async</c> job — so it is the throughput path for bulk loads. See
    /// docs/backend/15-medplum-integration-plan.md §5.
    /// </summary>
    [JsonPropertyName("medplumWriteMode")]
    public string? WriteMode { get; init; }

    /// <summary>Entries per batch Bundle in <c>async_batch</c> mode. Defaults to 100; clamped to [1, 500].</summary>
    [JsonPropertyName("medplumBatchSize")]
    public int? BatchSize { get; init; }

    /// <summary>True when this destination uses the async batch-bundle write path.</summary>
    public bool UsesAsyncBatch =>
        string.Equals(WriteMode, "async_batch", StringComparison.OrdinalIgnoreCase);

    /// <summary>The effective, clamped batch size for <c>async_batch</c> mode.</summary>
    public int EffectiveBatchSize => Math.Clamp(BatchSize ?? 100, 1, 500);

    /// <summary>
    /// Parses the flat metadata JSON; returns an all-null instance when null/blank/malformed. Each field is read
    /// tolerantly under either its bare key (<c>medplumClientId</c> — used by the POC and direct callers) or the
    /// portal's <c>dest_</c>-prefixed key (<c>dest_medplumClientId</c> — the same convention CSV's <c>dest_deliveryMode</c>
    /// uses), and numeric fields accept a JSON string or number, since the portal serializes its config bag as strings.
    /// </summary>
    public static MedplumConnectionMetadata Parse(string? connectionMetadataJson)
    {
        if (string.IsNullOrWhiteSpace(connectionMetadataJson))
        {
            return new MedplumConnectionMetadata();
        }

        Dictionary<string, JsonElement> map;
        try
        {
            using var doc = JsonDocument.Parse(connectionMetadataJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new MedplumConnectionMetadata();
            }

            map = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                map[property.Name] = property.Value.Clone();
            }
        }
        catch (JsonException)
        {
            return new MedplumConnectionMetadata();
        }

        return new MedplumConnectionMetadata
        {
            ClientId = ReadString(map, "medplumClientId"),
            TokenUrl = ReadString(map, "medplumTokenUrl"),
            IdentifierSystem = ReadString(map, "medplumIdentifierSystem"),
            AuthMethod = ReadString(map, "medplumAuthMethod"),
            KeyId = ReadString(map, "medplumKeyId"),
            WriteMode = ReadString(map, "medplumWriteMode"),
            BatchSize = ReadInt(map, "medplumBatchSize"),
        };
    }

    // Reads a string value under the bare key or its dest_-prefixed variant (JSON string or number both accepted).
    private static string? ReadString(IReadOnlyDictionary<string, JsonElement> map, string bareKey)
    {
        if (!map.TryGetValue(bareKey, out var element) && !map.TryGetValue($"dest_{bareKey}", out element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(IReadOnlyDictionary<string, JsonElement> map, string bareKey)
    {
        var raw = ReadString(map, bareKey);
        return int.TryParse(raw, out var value) ? value : null;
    }

    /// <summary>Resolves the token endpoint, deriving it from the FHIR base URL when not explicitly configured.</summary>
    public string ResolveTokenUrl(string fhirBaseUrl)
    {
        if (!string.IsNullOrWhiteSpace(TokenUrl))
        {
            return TokenUrl!;
        }

        var trimmed = fhirBaseUrl.TrimEnd('/');
        const string fhirSuffix = "/fhir/R4";
        var root = trimmed.EndsWith(fhirSuffix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[..^fhirSuffix.Length]
            : trimmed;
        return $"{root.TrimEnd('/')}/oauth2/token";
    }
}
