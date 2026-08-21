namespace FHIRBridge.Application.Abstractions.Terminology;

public interface ISnomedSynchronizationService
{
    Task<SnomedImportResult> SynchronizeAsync(CancellationToken cancellationToken);
}
