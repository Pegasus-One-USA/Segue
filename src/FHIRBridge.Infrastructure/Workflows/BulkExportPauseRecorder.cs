using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Storage;

namespace FHIRBridge.Infrastructure.Workflows;

public sealed class BulkExportPauseRecorder : IBulkExportPauseRecorder
{
    private readonly IBulkExportJobRepository _repository;

    public BulkExportPauseRecorder(IBulkExportJobRepository repository)
    {
        _repository = repository;
    }

    public async Task RecordPauseAsync(Guid bulkExportJobId, string priorNodeOutputsJson, CancellationToken cancellationToken)
    {
        var job = await _repository.GetAsync(bulkExportJobId, cancellationToken);
        if (job is null)
        {
            return;
        }

        job.RecordPriorNodeOutputs(priorNodeOutputsJson);
        await _repository.UpdateAsync(job, cancellationToken);
    }
}
