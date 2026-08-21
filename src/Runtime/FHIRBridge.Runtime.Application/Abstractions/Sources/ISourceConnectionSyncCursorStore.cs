namespace FHIRBridge.Runtime.Application.Abstractions.Sources;

/// <summary>
/// Records that a Backend System source connection's retrieval completed successfully, advancing its incremental
/// (<c>_lastUpdated</c>) cursor for the next run. Mirrors <see cref="ISourceConnectionRuntimeResolver"/>'s role as
/// the bridge from the workflow engine back to the control-plane SourceConnection.
/// </summary>
public interface ISourceConnectionSyncCursorStore
{
    Task RecordSuccessfulSyncAsync(
        Guid sourceConnectionId,
        IReadOnlyCollection<string> resourceTypes,
        DateTime syncedAtUtc,
        CancellationToken cancellationToken);
}
