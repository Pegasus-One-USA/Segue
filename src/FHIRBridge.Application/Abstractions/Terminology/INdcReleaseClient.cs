namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record NdcReleaseMetadata(Uri DownloadUrl, DateTime? ReleaseDateUtc);

/// <summary>Client for openFDA's public bulk-download manifest for the NDC Directory. Unlike RxNorm/SNOMED's UTS
/// API, openFDA is fully public — no API key is required to check for or download a release; an optional key
/// (if configured) is only added to raise the caller's rate limit.</summary>
public interface INdcReleaseClient
{
    Task<NdcReleaseMetadata> GetCurrentReleaseAsync(CancellationToken cancellationToken);
    Task<string> DownloadReleaseAsync(NdcReleaseMetadata release, CancellationToken cancellationToken);
}
