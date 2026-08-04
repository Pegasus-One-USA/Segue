namespace FHIRBridge.Application.Abstractions.Terminology;

public sealed record LoincSynchronizationResult(string Version, int ImportedConceptCount, bool AlreadyCurrent);
public interface ILoincSynchronizationService
{
    Task<LoincSynchronizationResult> SynchronizeAsync(CancellationToken cancellationToken);
}
