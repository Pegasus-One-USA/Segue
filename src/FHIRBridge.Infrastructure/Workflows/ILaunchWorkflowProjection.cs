using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// Projects a source connection's configured routes/mappings into a ranked-workflow graph (Scenario B).
/// The result is a canonical <c>Source → DeIdentification → Mapping → Destination</c> chain per mapping,
/// shaped to pass <c>WorkflowGraphValidator</c>, so the same designed model can drive a launch.
/// </summary>
public interface ILaunchWorkflowProjection
{
    /// <summary>
    /// Builds (in memory, not persisted) a workflow graph for the source's enabled routes. Returns null when
    /// the source has no enabled route/mapping that projects onto a currently-supported source/destination node.
    /// </summary>
    Task<WorkflowDefinition?> ProjectForSourceAsync(Guid sourceConnectionId, CancellationToken cancellationToken);
}
