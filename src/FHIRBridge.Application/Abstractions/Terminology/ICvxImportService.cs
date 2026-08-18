namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record CvxImportResult(string Version, int ImportedConceptCount);
public sealed record CvxImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface ICvxImportService
{
    /// <summary>Imports the CDC CVX code-set export archive already saved at <paramref name="zipFilePath"/>.
    /// Deletes the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<CvxImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
