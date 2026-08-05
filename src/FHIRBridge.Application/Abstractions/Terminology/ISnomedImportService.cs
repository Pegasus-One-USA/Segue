namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record SnomedImportResult(string Version, int ImportedConceptCount);

public sealed record SnomedImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface ISnomedImportService
{
    /// <summary>Imports the RF2 Snapshot release zip already saved at <paramref name="zipFilePath"/>.
    /// Deletes the file when done (success or failure). Runs off the request thread via
    /// FHIRBridge.Infrastructure.Terminology.TerminologyImportChannel — the caller enqueues the job and
    /// does not await this directly from an HTTP request.</summary>
    Task<SnomedImportResult> ImportAsync(string zipFilePath, CancellationToken cancellationToken);
}
