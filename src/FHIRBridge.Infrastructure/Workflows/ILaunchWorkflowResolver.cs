using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// Resolves the persisted workflow graph a launch should execute for a source connection (Scenario B),
/// projecting-and-persisting one from the source's routes on first use so it is durable and reusable.
/// </summary>
public interface ILaunchWorkflowResolver
{
    Task<WorkflowDefinition?> ResolveForSourceAsync(Guid sourceConnectionId, CancellationToken cancellationToken);
}
