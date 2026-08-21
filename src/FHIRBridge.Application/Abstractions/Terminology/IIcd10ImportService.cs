namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record Icd10ImportResult(string Version, int ImportedCodeCount);

public sealed record Icd10ImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedCodeCount, string Status, string? ErrorMessage);

public interface IIcd10ImportService
{
    /// <summary>Imports the ICD-10-CM release zip already saved at <paramref name="zipFilePath"/>. Deletes
    /// the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<Icd10ImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
