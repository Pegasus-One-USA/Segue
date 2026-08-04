namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record SnomedImportResult(string Version, int ImportedConceptCount);

public sealed record SnomedImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface ISnomedImportService
{
    Task<SnomedImportResult> ImportAsync(Stream releaseZipStream, CancellationToken cancellationToken);
}
