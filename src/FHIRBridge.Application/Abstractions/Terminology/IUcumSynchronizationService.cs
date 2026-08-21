namespace FHIRBridge.Application.Abstractions.Terminology;

public interface IUcumSynchronizationService
{
    Task<UcumImportResult> SynchronizeAsync(CancellationToken cancellationToken);
}
