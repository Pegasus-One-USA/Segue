namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record HcpcsImportResult(string Version, int ImportedConceptCount);
public sealed record HcpcsImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface IHcpcsImportService
{
    /// <summary>Imports the CMS HCPCS Level II release zip already saved at <paramref name="zipFilePath"/>.
    /// Deletes the file when done. Runs off the request thread via TerminologyImportChannel.</summary>
    Task<HcpcsImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
