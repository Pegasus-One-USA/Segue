namespace FHIRBridge.Application.Abstractions.Terminology;

public interface IRxNormSynchronizationService
{
    Task<RxNormImportResult> SynchronizeAsync(CancellationToken cancellationToken);
}
