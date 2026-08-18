namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record UcumReleaseMetadata(Uri DownloadUrl, string? TagName, DateTime? PublishedOnUtc);

/// <summary>Client for the public GitHub Releases API against ucum-org/ucum — no auth needed for a public repo,
/// just a User-Agent header (GitHub's API requires one on every request). If the latest release has no XML
/// asset attached, falls back to the repo's main-branch <c>ucum-essence.xml</c> file directly.</summary>
public interface IUcumReleaseClient
{
    Task<UcumReleaseMetadata> GetCurrentReleaseAsync(CancellationToken cancellationToken);
    Task<string> DownloadReleaseAsync(UcumReleaseMetadata release, CancellationToken cancellationToken);
}
