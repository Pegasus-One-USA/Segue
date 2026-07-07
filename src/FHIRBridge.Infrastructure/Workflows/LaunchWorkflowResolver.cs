using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Infrastructure.Workflows;

public sealed class LaunchWorkflowResolver : ILaunchWorkflowResolver
{
    private readonly ILaunchWorkflowProjection _projection;
    private readonly IWorkflowDefinitionStore _definitionStore;

    public LaunchWorkflowResolver(
        ILaunchWorkflowProjection projection,
        IWorkflowDefinitionStore definitionStore)
    {
        _projection = projection;
        _definitionStore = definitionStore;
    }

    public async Task<WorkflowDefinition?> ResolveForSourceAsync(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        // A source maps to a single durable launch graph. Reused as-is once created (so operator edits in the
        // builder survive); delete it to force a fresh projection from the current routes.
        var launchName = LaunchWorkflowNaming.ForSource(sourceConnectionId);

        var existing = (await _definitionStore.ListAsync(cancellationToken))
            .FirstOrDefault(workflow => string.Equals(workflow.Name, launchName, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            return existing;
        }

        var projected = await _projection.ProjectForSourceAsync(sourceConnectionId, cancellationToken);
        if (projected is null)
        {
            return null;
        }

        // The projection already names the graph via LaunchWorkflowNaming, so persist it as-is for reuse.
        return await _definitionStore.SaveAsync(projected, cancellationToken);
    }
}
