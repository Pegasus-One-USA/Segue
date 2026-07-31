namespace FHIRBridge.Runtime.Application.Workflows.Storage;

/// <summary>
/// Lets the orchestrator record, onto an already-persisted <c>BulkExportJob</c> row (owned by the "main"
/// FHIRBridge.Application/Infrastructure stack, not this Runtime plane), the outputs of every node that completed
/// before the node that deferred to it — so a later resume can seed <c>outputsByNodeId</c> without recomputing
/// them. Kept as a small Runtime-local abstraction (rather than the orchestrator depending on
/// <c>IBulkExportJobRepository</c> directly) so <c>FHIRBridge.Runtime.Application</c> stays independent of the
/// other stack; the implementation lives in <c>FHIRBridge.Infrastructure</c>, which already depends on both.
/// </summary>
public interface IBulkExportPauseRecorder
{
    Task RecordPauseAsync(Guid bulkExportJobId, string priorNodeOutputsJson, CancellationToken cancellationToken);
}
