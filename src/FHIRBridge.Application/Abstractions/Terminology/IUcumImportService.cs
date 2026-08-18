namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record UcumImportResult(string Version, int ImportedConceptCount);
public sealed record UcumImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface IUcumImportService
{
    /// <summary>Imports the <c>ucum-essence.xml</c> file already saved at <paramref name="filePath"/>. Deletes
    /// the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<UcumImportResult> ImportAsync(string filePath, CancellationToken cancellationToken);
}
