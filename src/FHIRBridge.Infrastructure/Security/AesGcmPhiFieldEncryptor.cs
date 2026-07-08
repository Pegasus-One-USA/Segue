using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field encryptor for PHI-bearing execution-history columns (raw fetched JSON, normalized JSON,
/// mapped values). The key is read once from <c>Security:PhiEncryptionKey</c> (a base64-encoded 256-bit key) —
/// the same "plaintext-in-config" pattern already used for <c>Authentication:SigningKey</c>, so it can be backed
/// by a Key Vault-fed configuration provider or environment variable in production. EF value converters must be
/// synchronous, which is why this reads a pre-provisioned key rather than resolving one via <see cref="ISecretProvider"/>
/// (which is async, Key-Vault-reference based, and meant for per-destination secrets, not row-level encryption keys).
/// </summary>
public sealed class AesGcmPhiFieldEncryptor : IPhiFieldEncryptor
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesGcmPhiFieldEncryptor(IConfiguration configuration)
    {
        var configuredKey = configuration["Security:PhiEncryptionKey"];
        _key = string.IsNullOrWhiteSpace(configuredKey)
            // Dev-only fallback so local/no-config environments still work; production must set a real key.
            ? SHA256.HashData("dev-only-insecure-phi-encryption-key"u8.ToArray())
            : Convert.FromBase64String(configuredKey);
    }

    public string Encrypt(string plaintext)
    {
        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[NonceSizeBytes + TagSizeBytes + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, NonceSizeBytes);
        Buffer.BlockCopy(tag, 0, payload, NonceSizeBytes, TagSizeBytes);
        Buffer.BlockCopy(ciphertext, 0, payload, NonceSizeBytes + TagSizeBytes, ciphertext.Length);

        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string ciphertext)
    {
        var payload = Convert.FromBase64String(ciphertext);
        var nonce = payload[..NonceSizeBytes];
        var tag = payload[NonceSizeBytes..(NonceSizeBytes + TagSizeBytes)];
        var cipherBytes = payload[(NonceSizeBytes + TagSizeBytes)..];
        var plaintextBytes = new byte[cipherBytes.Length];

        using var aesGcm = new AesGcm(_key, TagSizeBytes);
        aesGcm.Decrypt(nonce, cipherBytes, tag, plaintextBytes);

        return System.Text.Encoding.UTF8.GetString(plaintextBytes);
    }
}
