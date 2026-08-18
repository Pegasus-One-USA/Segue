namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record ReleaseFreshnessResult(bool NewerReleaseFound, string? LatestKnownFileName, DateTime CheckedOnUtc);

/// <summary>
/// For vocabularies with no version-check API (ICD-10-CM, ICD-10-PCS, HCPCS Level II) — fetches a CMS/NCHS
/// listing page, extracts referenced archive/file names, and compares the most recent one against whatever was
/// last seen (stored under <c>Terminology:{settingsKeyPrefix}:LastKnownFileName</c>/<c>LastCheckedUtc</c> in the
/// generic SystemSettings store). This does not download or import anything — it only answers "does a newer file
/// appear to exist," leaving the actual import to the existing manual-upload flow.
/// </summary>
public interface IReleaseFreshnessChecker
{
    /// <summary>
    /// <paramref name="requiredKeywords"/> — a candidate file name must contain ALL of these (case-insensitive)
    /// to be considered the release archive. Required because these listing pages mix the actual release with
    /// unrelated auxiliary files (e.g. ICD-10-CM's CDC directory also lists <c>POAexemptCodesFY26.zip</c>,
    /// guideline PDFs, and addenda alongside <c>icd10cm-Code Descriptions-2026.zip</c>) — without this filter,
    /// picking "the most recent-looking file name" can silently latch onto the wrong file.
    /// </summary>
    Task<ReleaseFreshnessResult> CheckAsync(string listingUrl, string settingsKeyPrefix, IReadOnlyList<string> requiredKeywords, CancellationToken cancellationToken);

    /// <summary>Reads the last-stored check result without hitting the network — for page-load display.</summary>
    Task<ReleaseFreshnessResult?> GetLastCheckAsync(string settingsKeyPrefix, CancellationToken cancellationToken);

    /// <summary>
    /// Downloads the release file identified by the most recent <see cref="CheckAsync"/> call (its resolved
    /// absolute URL, stored alongside the display file name) to <c>App_Data/Terminology/{destinationFolderName}/</c>
    /// and returns the local path. Throws <see cref="InvalidOperationException"/> if no successful check has run
    /// yet — callers should prompt "Check for Updates" first rather than guessing a URL.
    /// </summary>
    Task<string> DownloadLatestAsync(string settingsKeyPrefix, string destinationFolderName, CancellationToken cancellationToken);
}
