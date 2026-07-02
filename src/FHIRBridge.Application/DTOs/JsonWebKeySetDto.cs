using System.Text.Json.Serialization;

namespace FHIRBridge.Application.DTOs;

/// <summary>
/// A JSON Web Key Set (RFC 7517) publishing the <em>public</em> keys FHIRBridge uses to sign SMART Backend Services
/// client assertions (<c>private_key_jwt</c>). A customer registers the source connection's <c>.well-known/jwks.json</c>
/// URL with their EHR so the authorization server can verify FHIRBridge's assertions. Only public key material is ever
/// serialized here; the private key never leaves the secret store.
/// </summary>
public sealed record JsonWebKeySetDto(
    [property: JsonPropertyName("keys")] IReadOnlyList<JsonWebKeyDto> Keys);

/// <summary>
/// A single public RSA key in JWK form (RFC 7517 / RFC 7518). <see cref="Modulus"/> and <see cref="Exponent"/> are the
/// base64url-encoded (unpadded) big-endian public parameters; <see cref="KeyId"/> matches the <c>kid</c> header of the
/// signed assertion when the source connection has one configured.
/// </summary>
public sealed record JsonWebKeyDto(
    [property: JsonPropertyName("kty")] string KeyType,
    [property: JsonPropertyName("use")] string Use,
    [property: JsonPropertyName("alg")] string Algorithm,
    [property: JsonPropertyName("kid")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? KeyId,
    [property: JsonPropertyName("n")] string Modulus,
    [property: JsonPropertyName("e")] string Exponent);
