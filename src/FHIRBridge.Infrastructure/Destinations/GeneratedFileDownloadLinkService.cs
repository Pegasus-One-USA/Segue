using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
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
    private readonly string _rootPath;

    public GeneratedFileDownloadLinkService(IOptions<GeneratedFileDownloadOptions> options)
    {
        _options = options.Value;
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
        await File.WriteAllBytesAsync(physicalPath, file.Content, cancellationToken);

        var payload = new TokenPayload(guid, createdUtc, createdUtc + expiry, file.ContentType, file.FileName);
        var token = Sign(payload);

        return $"{_options.PublicBaseUrl.TrimEnd('/')}/api/v1/generated-files/{token}";
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

    private string Sign(TokenPayload payload)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        var signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.SigningSecret), payloadBytes);

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

        var expectedSignature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(_options.SigningSecret), payloadBytes);
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
