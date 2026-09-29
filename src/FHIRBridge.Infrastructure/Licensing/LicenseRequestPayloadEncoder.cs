using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>Everything the licensor needs to auto-fill the mint form for one license request — mirrors
/// exactly what the direct <c>POST /api/v1/license-request</c> call already sends; this is the same
/// payload, just for the manual-fallback path when that call can't reach the licensor.</summary>
public sealed record LicenseRequestPayload(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber, string UniqueKey,
    string? RequestHost = null);

/// <summary>
/// Encodes/decodes <see cref="LicenseRequestPayload"/> into a single copy-pasteable opaque string, for the
/// operator to share with the licensor by email/support ticket when the direct API call fails. AES-256-GCM
/// with <see cref="LicenseRequestSharedKey"/> — see that class's remarks for exactly what security property
/// this does (and does not) provide. Layout: base64(12-byte nonce || ciphertext || 16-byte tag).
/// </summary>
public static class LicenseRequestPayloadEncoder
{
    private static readonly byte[] Key = Convert.FromBase64String(LicenseRequestSharedKey.KeyBase64);
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    public static string Encode(LicenseRequestPayload payload)
    {
        var plaintext = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using (var aesGcm = new AesGcm(Key, TagSizeBytes))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var combined = new byte[nonce.Length + ciphertext.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, combined, 0, nonce.Length);
        Buffer.BlockCopy(ciphertext, 0, combined, nonce.Length, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, combined, nonce.Length + ciphertext.Length, tag.Length);

        return Convert.ToBase64String(combined);
    }

    /// <summary>Returns null for anything that doesn't decode/decrypt/parse cleanly — a corrupted paste, a
    /// blob encoded with a since-rotated key, or plain garbage all fail the same way, never throw.</summary>
    public static LicenseRequestPayload? TryDecode(string encoded)
    {
        try
        {
            var combined = Convert.FromBase64String(encoded.Trim());
            if (combined.Length < NonceSizeBytes + TagSizeBytes)
            {
                return null;
            }

            var nonce = combined.AsSpan(0, NonceSizeBytes);
            var tag = combined.AsSpan(combined.Length - TagSizeBytes, TagSizeBytes);
            var ciphertext = combined.AsSpan(NonceSizeBytes, combined.Length - NonceSizeBytes - TagSizeBytes);
            var plaintext = new byte[ciphertext.Length];

            using (var aesGcm = new AesGcm(Key, TagSizeBytes))
            {
                aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            }

            return JsonSerializer.Deserialize<LicenseRequestPayload>(plaintext);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }
}
