namespace FHIRBridge.Application.Abstractions.Destinations;

/// <summary>
/// Persists a <see cref="GeneratedFile"/> to disk and mints a signed, time-limited download URL for it, without a
/// database record — the token itself carries everything needed to locate and validate the file (see the
/// implementation for the exact token shape). The real, on-disk filename/path is never exposed in the token or URL.
/// </summary>
public interface IGeneratedFileDownloadLinkService
{
    Task<string> CreateLinkAsync(GeneratedFile file, TimeSpan expiry, CancellationToken cancellationToken);

    /// <summary>
    /// Validates the token's signature and expiry and resolves it back to the physical file. Returns false (and
    /// deletes the file, if found, past its expiry) for an invalid, tampered, or expired token.
    /// </summary>
    Task<GeneratedFileDownloadResolution?> TryResolveAsync(string token, CancellationToken cancellationToken);

    /// <summary>Decrypts the resolved file's on-disk ciphertext back to raw bytes for streaming to the caller.</summary>
    Task<byte[]> ReadDecryptedAsync(string physicalPath, CancellationToken cancellationToken);
}

public sealed record GeneratedFileDownloadResolution(string PhysicalPath, string ContentType, string DisplayFileName);
