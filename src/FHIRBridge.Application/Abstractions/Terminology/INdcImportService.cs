namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record NdcImportResult(string Version, int ImportedConceptCount);
public sealed record NdcImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface INdcImportService
{
    /// <summary>Imports the openFDA NDC Directory bulk-export zip already saved at <paramref name="zipFilePath"/>.
    /// Deletes the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<NdcImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
