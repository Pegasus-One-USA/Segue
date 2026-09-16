using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Application.Services.Workflows;

/// <inheritdoc />
public sealed class WorkflowGraphVersionMigrationService : IWorkflowGraphVersionMigrationService
{
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;

    public WorkflowGraphVersionMigrationService(IWorkflowDefinitionStore workflowDefinitionStore)
    {
        _workflowDefinitionStore = workflowDefinitionStore;
    }

    public async Task<WorkflowGraphVersionMigrationResult> ConvertAsync(
        bool dryRun,
        Guid? workflowId,
        CancellationToken cancellationToken)
    {
        var workflows = workflowId is { } id
            ? [await _workflowDefinitionStore.GetAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Workflow '{id}' does not exist.")]
            : (IReadOnlyCollection<WorkflowDefinition>)await _workflowDefinitionStore.ListAsync(cancellationToken);

        var reports = new List<WorkflowGraphVersionReport>(workflows.Count);
        var written = 0;

        foreach (var workflow in workflows)
        {
            if (!WorkflowGraphVersionConverter.RequiresConversion(workflow))
            {
                reports.Add(new WorkflowGraphVersionReport(workflow.Id, workflow.Name, RequiresConversion: false, []));
                continue;
            }

            var collapsible = WorkflowGraphVersionConverter.CollapsibleNodes(workflow);
            var collapsedNames = collapsible
                .Select(node => $"{node.DisplayName} ({node.NodeType})")
                .ToList();

            var blocker = WorkflowGraphVersionConverter.DescribeBlocker(workflow);
            reports.Add(new WorkflowGraphVersionReport(
                workflow.Id, workflow.Name, RequiresConversion: true, collapsedNames, blocker));

            if (dryRun || blocker is not null)
            {
                continue;
            }

            await ConvertAsync(workflow, collapsible, cancellationToken);
            written++;
        }

        return new WorkflowGraphVersionMigrationResult(dryRun, reports, written);
    }

    /// <summary>
    /// Rebuilds the graph with the collapsible V1 nodes replaced by ONE FhirResourceTransformNode, and rewires
    /// the edges around them so the chain stays connected.
    /// </summary>
    private async Task ConvertAsync(
        WorkflowDefinition workflow,
        IReadOnlyList<WorkflowNode> collapsible,
        CancellationToken cancellationToken)
    {
        var collapsedIds = collapsible.Select(node => node.Id).ToHashSet();

        // The first collapsible node's id is REUSED for the replacement, so anything already referencing it —
        // WorkflowNodeRuns, FieldLineageEntries, an issued checkpoint url — still resolves to the step that
        // took its place, rather than being orphaned.
        var survivor = collapsible[0];

        var rebuilt = new WorkflowDefinition(
            workflow.Id, workflow.Name, workflow.Version, workflow.IsEnabled, workflow.IsPubliclyLaunchable, workflow.Description);
        rebuilt.SetTrigger(workflow.Trigger);

        foreach (var node in workflow.Nodes)
        {
            if (collapsedIds.Contains(node.Id) && node.Id != survivor.Id)
            {
                continue;
            }

            var isSurvivor = node.Id == survivor.Id;
            rebuilt.AddNodeWithId(
                node.Id,
                isSurvivor ? WorkflowNodeTypes.FhirResourceTransform : node.NodeType,
                node.Category,
                isSurvivor ? WorkflowGraphVersionConverter.TransformationRank : node.Rank,
                node.SubRank,
                isSurvivor ? "Transformation" : node.DisplayName,
                node.ConfigurationJson,
                node.PositionX,
                node.PositionY,
                node.IsEnabled,
                node.CheckpointUrlEnabled);
        }

        foreach (var edge in RewireEdges(workflow, collapsedIds, survivor.Id))
        {
            rebuilt.AddEdge(edge.From, edge.To);
        }

        await _workflowDefinitionStore.SaveAsync(rebuilt, cancellationToken);
    }

    /// <summary>
    /// Redirects every edge that touched a collapsed node onto the survivor, then drops the self-edges that
    /// leaves behind (an edge between two collapsed nodes becomes survivor→survivor) and any duplicates.
    /// Without this, collapsing normalize→terminology would delete the only path from source to destination.
    /// </summary>
    private static IEnumerable<(Guid From, Guid To)> RewireEdges(
        WorkflowDefinition workflow,
        IReadOnlySet<Guid> collapsedIds,
        Guid survivorId)
    {
        var seen = new HashSet<(Guid, Guid)>();

        foreach (var edge in workflow.Edges)
        {
            var from = collapsedIds.Contains(edge.FromNodeId) ? survivorId : edge.FromNodeId;
            var to = collapsedIds.Contains(edge.ToNodeId) ? survivorId : edge.ToNodeId;

            if (from == to || !seen.Add((from, to)))
            {
                continue;
            }

            yield return (from, to);
        }
    }
}
