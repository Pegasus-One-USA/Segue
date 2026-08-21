using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Options;

namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// Persists a <see cref="GeneratedFile"/> under a date-partitioned folder (so files never pile up in one giant
/// directory) and mints an HMAC-signed token for it — the same signing primitives
/// (<see cref="HMACSHA256"/> + <see cref="CryptographicOperations.FixedTimeEquals"/>) already used by
/// <c>WebhookIngestionController</c>. The token carries the guid, creation timestamp (to relocate the file without a
/// database record), expiry, content type, and display filename; the real on-disk path is derived only from the
/// guid, so a client can never guess or enumerate physical filenames from the token/URL.
/// </summary>
public sealed class GeneratedFileDownloadLinkService : IGeneratedFileDownloadLinkService
{
    private readonly GeneratedFileDownloadOptions _options;
    private readonly IAppSecretAccessor _secretAccessor;
    private readonly ISystemSettingsCache _settingsCache;
    private readonly IPhiFieldEncryptor _encryptor;
    private readonly string _rootPath;

    public GeneratedFileDownloadLinkService(
        IOptions<GeneratedFileDownloadOptions> options,
        IAppSecretAccessor secretAccessor,
        ISystemSettingsCache settingsCache,
        IPhiFieldEncryptor encryptor)
    {
        _options = options.Value;
        _secretAccessor = secretAccessor;
        _settingsCache = settingsCache;
        _encryptor = encryptor;
        // A relative RootPath must not be resolved against the process's current working directory — that varies
        // by how the host is launched (console vs IIS vs Windows Service) — so it's anchored to the app's own base
        // directory instead. PhysicalFileResult (used to serve the file back) requires an absolute path.
        _rootPath = Path.IsPathRooted(_options.RootPath)
            ? _options.RootPath
            : Path.Combine(AppContext.BaseDirectory, _options.RootPath);
    }

    public async Task<string> CreateLinkAsync(GeneratedFile file, TimeSpan expiry, CancellationToken cancellationToken)
    {
        var guid = Guid.NewGuid();
        var createdUtc = DateTimeOffset.UtcNow;
        var extension = string.IsNullOrEmpty(Path.GetExtension(file.FileName)) ? string.Empty : Path.GetExtension(file.FileName);

        var directory = Path.Combine(_rootPath, createdUtc.ToString("yyyy"), createdUtc.ToString("MM"), createdUtc.ToString("dd"));
        Directory.CreateDirectory(directory);
        var physicalPath = Path.Combine(directory, guid.ToString("N") + extension);
        // HIPAA #6: encrypt at rest — content is base64'd, run through the same AES-GCM encryptor already used
        // for PHI database columns, then written as UTF-8 ciphertext rather than plaintext bytes.
        var ciphertext = _encryptor.Encrypt(Convert.ToBase64String(file.Content));
        await File.WriteAllTextAsync(physicalPath, ciphertext, Encoding.UTF8, cancellationToken);

        var payload = new TokenPayload(guid, createdUtc, createdUtc + expiry, file.ContentType, file.FileName);
        var token = Sign(payload);

        var publicBaseUrl = await _settingsCache.GetStringAsync(
            "GeneratedFileDownload:PublicBaseUrl", _options.PublicBaseUrl, cancellationToken);

        return $"{publicBaseUrl.TrimEnd('/')}/api/v1/generated-files/{token}";
    }

    public Task<GeneratedFileDownloadResolution?> TryResolveAsync(string token, CancellationToken cancellationToken)
    {
        var payload = Verify(token);
        if (payload is null)
        {
            return Task.FromResult<GeneratedFileDownloadResolution?>(null);
        }

        var extension = string.IsNullOrEmpty(Path.GetExtension(payload.DisplayFileName)) ? string.Empty : Path.GetExtension(payload.DisplayFileName);
        var physicalPath = Path.Combine(
            _rootPath,
            payload.CreatedUtc.ToString("yyyy"),
            payload.CreatedUtc.ToString("MM"),
            payload.CreatedUtc.ToString("dd"),
            payload.Guid.ToString("N") + extension);

        if (payload.ExpiryUtc < DateTimeOffset.UtcNow)
        {
            if (File.Exists(physicalPath))
            {
                File.Delete(physicalPath);
            }

            return Task.FromResult<GeneratedFileDownloadResolution?>(null);
        }

        if (!File.Exists(physicalPath))
        {
            return Task.FromResult<GeneratedFileDownloadResolution?>(null);
        }

        return Task.FromResult<GeneratedFileDownloadResolution?>(
            new GeneratedFileDownloadResolution(physicalPath, payload.ContentType, payload.DisplayFileName));
    }

    /// <summary>
    /// Decrypts a resolved file's on-disk ciphertext back to raw bytes. Called by the controller immediately
    /// before streaming the response — content never sits decrypted anywhere but the response body.
    /// </summary>
    public async Task<byte[]> ReadDecryptedAsync(string physicalPath, CancellationToken cancellationToken)
    {
        var text = await File.ReadAllTextAsync(physicalPath, Encoding.UTF8, cancellationToken);

        try
        {
            return Convert.FromBase64String(_encryptor.Decrypt(text));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            // Backward-compatible rollout: files written before encryption-at-rest shipped are still on disk as
            // plaintext until their existing TTL expires them. Fall back to reading raw bytes for those.
            return await File.ReadAllBytesAsync(physicalPath, cancellationToken);
        }
    }

    private string Sign(TokenPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secretAccessor.DownloadLinkSigningSecret), payloadBytes);

        return $"{Convert.ToHexString(payloadBytes)}.{Convert.ToHexString(signature)}";
    }

    private TokenPayload? Verify(string token)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2)
        {
            return null;
        }

        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            payloadBytes = Convert.FromHexString(parts[0]);
            providedSignature = Convert.FromHexString(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        var expectedSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_secretAccessor.DownloadLinkSigningSecret), payloadBytes);
        if (!CryptographicOperations.FixedTimeEquals(providedSignature, expectedSignature))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TokenPayload>(payloadBytes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record TokenPayload(
        Guid Guid,
        DateTimeOffset CreatedUtc,
        DateTimeOffset ExpiryUtc,
        string ContentType,
        string DisplayFileName);
}
