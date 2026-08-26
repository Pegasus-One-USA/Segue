using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// AES-256-GCM field encryptor for PHI-bearing execution-history columns (raw fetched JSON, normalized JSON,
/// mapped values). The key is <see cref="AppSecretReferences.PhiEncryptionKey"/>, auto-generated on first boot
/// and cached in <see cref="IAppSecretAccessor"/> by <c>AppSecretProvisioner</c> — the same pattern used for the
/// JWT signing key and other app secrets. Reading the already-provisioned, synchronous accessor (rather than
/// resolving one via <see cref="ISecretProvider"/> directly, which is async) keeps this usable from EF value
/// converters, which must be synchronous.
/// </summary>
public sealed class AesGcmPhiFieldEncryptor : IPhiFieldEncryptor
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;

    private readonly byte[] _key;

    public AesGcmPhiFieldEncryptor(IAppSecretAccessor appSecretAccessor)
    {
        _key = Convert.FromBase64String(appSecretAccessor.PhiEncryptionKey);
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
