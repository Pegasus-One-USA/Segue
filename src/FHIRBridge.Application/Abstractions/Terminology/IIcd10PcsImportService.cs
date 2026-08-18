namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record Icd10PcsImportResult(string Version, int ImportedConceptCount);
public sealed record Icd10PcsImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface IIcd10PcsImportService
{
    /// <summary>Imports the CMS ICD-10-PCS code-table release zip already saved at <paramref name="zipFilePath"/>.
    /// Deletes the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<Icd10PcsImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
