namespace FHIRBridge.Application.Abstractions.Terminology;

public interface INdcSynchronizationService
{
    Task<NdcImportResult> SynchronizeAsync(CancellationToken cancellationToken);
}
