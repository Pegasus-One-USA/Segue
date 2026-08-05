namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record RxNormImportResult(string Version, int ImportedConceptCount);

public sealed record RxNormImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface IRxNormImportService
{
    /// <summary>Imports the RxNorm RRF release zip already saved at <paramref name="zipFilePath"/>.
    /// Deletes the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<RxNormImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
