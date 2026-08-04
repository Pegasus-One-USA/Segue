namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record LoincReleaseMetadata(string Version, DateTime? ReleaseDateUtc, int NumberOfLoincs, Uri DownloadUrl, string Md5Hash);
public interface ILoincReleaseClient
{
    Task<LoincReleaseMetadata> GetCurrentReleaseAsync(CancellationToken cancellationToken);
    Task<string> DownloadReleaseAsync(LoincReleaseMetadata release, CancellationToken cancellationToken);
}
