namespace FHIRBridge.Application.Abstractions.Terminology;

/// <summary>
/// A release NLM's UMLS Terminology Services (UTS) reports as "current" for a given <c>releaseType</c>
/// (e.g. <c>rxnorm-full-monthly-release</c>, <c>snomed-ct-us-edition</c>) via <c>uts-ws.nlm.nih.gov/releases</c>.
/// </summary>
public sealed record UtsReleaseMetadata(Uri DownloadUrl, string? ReleaseName, DateTime? ReleaseDateUtc);

/// <summary>
/// Shared client for NLM's UTS release-check/download API — RxNorm and SNOMED CT (US Edition) both auto-poll
/// through this single endpoint family, differing only in the <c>releaseType</c> value and destination folder.
/// Unlike LOINC's Download API (which reports version/checksum up front), UTS's <c>/releases</c> response only
/// reliably carries a download URL — the actual release version is extracted downstream, from inside the
/// downloaded archive itself, by the existing <see cref="IRxNormImportService"/>/<see cref="ISnomedImportService"/>
/// import pipelines (unchanged). So there is no "already current, skip" short-circuit here: every synchronization
/// run downloads and re-imports, which the existing import services already handle idempotently.
/// </summary>
public interface IUtsReleaseClient
{
    Task<UtsReleaseMetadata> GetCurrentReleaseAsync(string releaseType, CancellationToken cancellationToken);

    /// <summary>Downloads to <c>App_Data/Terminology/{vocabularyFolderName}/</c> and returns the saved file path.</summary>
    Task<string> DownloadReleaseAsync(UtsReleaseMetadata release, string vocabularyFolderName, CancellationToken cancellationToken);
}
