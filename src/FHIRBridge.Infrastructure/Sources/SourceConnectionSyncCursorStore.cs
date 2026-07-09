using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Abstractions.Sources;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Advances a Backend System source connection's incremental-sync cursor after a workflow run completes
/// successfully. A no-op when the connection has no retrieval configuration (interactive sources, or Backend
/// sources created before this field existed) or has since been deleted.
/// </summary>
public sealed class SourceConnectionSyncCursorStore : ISourceConnectionSyncCursorStore
{
    private readonly IConfigurationRepository _repository;

    public SourceConnectionSyncCursorStore(IConfigurationRepository repository)
    {
        _repository = repository;
    }

    public async Task RecordSuccessfulSyncAsync(Guid sourceConnectionId, DateTime syncedAtUtc, CancellationToken cancellationToken)
    {
        var sourceConnection = await _repository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return;
        }

        sourceConnection.RecordRetrievalSync(syncedAtUtc);
        await _repository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);
    }
}
