namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record LoincSynchronizationResult(string Version, int ImportedConceptCount, bool AlreadyCurrent);
public sealed record LoincImportHistoryEntryDto(Guid Id, string? Version, DateTime StartedOnUtc, DateTime? CompletedOnUtc, int ImportedConceptCount, string Status, string? ErrorMessage);

public interface ILoincSynchronizationService
{
    Task<LoincSynchronizationResult> SynchronizeAsync(CancellationToken cancellationToken);
}
