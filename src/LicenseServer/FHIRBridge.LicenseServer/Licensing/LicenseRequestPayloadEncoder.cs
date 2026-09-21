using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>Everything a customer install's License Request screen sends — mirrors exactly the main repo's
/// own <c>LicenseRequestPayload</c> record (src/FHIRBridge.Infrastructure/Licensing/LicenseRequestPayloadEncoder.cs),
/// field for field, since this decodes the same blob that record's <c>Encode</c> produced.</summary>
public sealed record LicenseRequestPayload(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber, string UniqueKey);

/// <summary>
/// Decodes the copy-pasteable manual-fallback blob a customer shares with support when their install's
/// direct <c>POST /api/license-requests</c> call couldn't reach this server. AES-256-GCM with
/// <see cref="LicenseRequestSharedKey"/> — layout: base64(12-byte nonce || ciphertext || 16-byte tag).
/// Only <c>TryDecode</c> is needed here; this server never encodes one itself.
/// </summary>
public static class LicenseRequestPayloadEncoder
{
    private static readonly byte[] Key = Convert.FromBase64String(LicenseRequestSharedKey.KeyBase64);
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

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
