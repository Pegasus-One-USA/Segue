using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using System.Text.Json;

namespace FHIRBridge.Runtime.Application.Workflows;

public sealed class WorkflowGraphValidator : IWorkflowGraphValidator
{
    private readonly IWorkflowNodeCatalog _catalog;

    public WorkflowGraphValidator()
        : this(new DefaultWorkflowNodeCatalog())
    {
    }

    public WorkflowGraphValidator(IWorkflowNodeCatalog catalog)
    {
        _catalog = catalog;
    }

    public WorkflowGraphValidationResult Validate(WorkflowDefinition workflowDefinition)
    {
        var errors = new List<string>();
        var enabledNodes = workflowDefinition.Nodes.Where(node => node.IsEnabled).ToDictionary(node => node.Id);
        var enabledEdges = workflowDefinition.Edges
            .Where(edge => enabledNodes.ContainsKey(edge.FromNodeId) && enabledNodes.ContainsKey(edge.ToNodeId))
            .ToArray();

        foreach (var node in enabledNodes.Values)
        {
            var catalogItem = _catalog.Find(node.NodeType);
            if (catalogItem is null)
            {
                errors.Add($"Node '{node.NodeType}' is not in the workflow node catalog.");
                continue;
            }

            if (!WorkflowRankPolicy.IsRankValidForCategory(node.Category, node.Rank))
            {
                errors.Add($"Node '{node.NodeType}' has rank {node.Rank}, which is invalid for category {node.Category}.");
            }

            if (node.Category != catalogItem.Category)
            {
                errors.Add($"Node '{node.NodeType}' category {node.Category} does not match catalog category {catalogItem.Category}.");
            }

            if (catalogItem.Rank != node.Rank)
            {
                errors.Add($"Node '{node.NodeType}' rank {node.Rank} does not match catalog rank {catalogItem.Rank}.");
            }

            foreach (var missingField in GetMissingConfigurationFields(node, catalogItem))
            {
                errors.Add($"Node '{node.NodeType}' is missing required configuration field '{missingField}'.");
            }

            if (node.Category == WorkflowNodeCategory.Source && enabledEdges.Any(edge => edge.ToNodeId == node.Id))
            {
                errors.Add($"Source node '{node.NodeType}' cannot require upstream input.");
            }
        }

        foreach (var edge in enabledEdges)
        {
            var fromNode = enabledNodes[edge.FromNodeId];
            var toNode = enabledNodes[edge.ToNodeId];

            if (toNode.Rank <= fromNode.Rank)
            {
                errors.Add($"Edge from '{fromNode.NodeType}' to '{toNode.NodeType}' violates rank ordering.");
            }

            var fromCatalog = _catalog.Find(fromNode.NodeType);
            var toCatalog = _catalog.Find(toNode.NodeType);
            if (fromCatalog is not null
                && toCatalog is not null
                && toCatalog.InputContracts.Count > 0
                && !AreContractsCompatible(fromCatalog.OutputContract, toCatalog, toNode))
            {
                errors.Add($"Node '{toNode.NodeType}' cannot accept output contract {fromCatalog.OutputContract} from '{fromNode.NodeType}'.");
            }

            if (fromNode.Category == WorkflowNodeCategory.Destination
                && toNode.NodeType == WorkflowNodeTypes.DeIdentification)
            {
                errors.Add("De-identification cannot run after a destination node.");
            }
        }

        foreach (var destination in enabledNodes.Values.Where(node =>
            node.Category is WorkflowNodeCategory.Destination or WorkflowNodeCategory.Analytics))
        {
            if (!HasUpstreamPath(enabledNodes, enabledEdges, destination.Id, node =>
                node.Category is WorkflowNodeCategory.Source or WorkflowNodeCategory.Transform or WorkflowNodeCategory.Compliance))
            {
                errors.Add($"Destination node '{destination.NodeType}' must have an upstream source, transform, or compliance path.");
            }

            if (!HasUpstreamPath(enabledNodes, enabledEdges, destination.Id, node =>
                string.Equals(node.NodeType, WorkflowNodeTypes.Mapping, StringComparison.OrdinalIgnoreCase))
                && DestinationRequiresMappedRecords(destination))
            {
                errors.Add($"Destination node '{destination.NodeType}' requires mapped records and must be downstream of a mapping node.");
            }
        }

        foreach (var deIdentificationNode in enabledNodes.Values.Where(node =>
            string.Equals(node.NodeType, WorkflowNodeTypes.DeIdentification, StringComparison.OrdinalIgnoreCase)))
        {
            if (HasDownstreamPath(enabledNodes, enabledEdges, deIdentificationNode.Id, node =>
                node.Category == WorkflowNodeCategory.Destination))
            {
                continue;
            }
        }

        if (HasCycle(enabledNodes.Keys, enabledEdges))
        {
            errors.Add("Workflow graph contains a cycle.");
        }

        return errors.Count == 0
            ? WorkflowGraphValidationResult.Success()
            : WorkflowGraphValidationResult.Failure(errors);
    }

    private static IReadOnlyCollection<string> GetMissingConfigurationFields(
        WorkflowNode node,
        WorkflowNodeCatalogItem catalogItem)
    {
        var keys = node.Configuration.Select(configuration => configuration.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var document = JsonDocument.Parse(node.ConfigurationJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    keys.Add(property.Name);
                }
            }
        }
        catch (JsonException)
        {
            return catalogItem.RequiredConfigurationFields.ToArray();
        }

        return catalogItem.RequiredConfigurationFields
            .Where(field => !keys.Contains(field))
            .ToArray();
    }

    private static bool DestinationRequiresMappedRecords(WorkflowNode destination)
        => destination.Category is WorkflowNodeCategory.Destination or WorkflowNodeCategory.Analytics
           // FhirRepositoryDestination is spec-owned (see docs/backend/14-mapping-profile-master-screen-plan.md) —
           // it accepts raw/normalized resources directly (see DefaultWorkflowNodeCatalog.Destination()'s widened
           // InputContracts for this node type), so it never needs an upstream Mapping node. Every other
           // destination/analytics node type is unaffected.
           && !string.Equals(destination.NodeType, WorkflowNodeTypes.FhirRepositoryDestination, StringComparison.OrdinalIgnoreCase);

    private static bool AreContractsCompatible(
        WorkflowDataContract fromContract,
        WorkflowNodeCatalogItem toCatalog,
        WorkflowNode toNode)
    {
        if (toCatalog.InputContracts.Contains(fromContract))
        {
            return true;
        }

        if (string.Equals(toNode.NodeType, WorkflowNodeTypes.Mapping, StringComparison.OrdinalIgnoreCase)
            || toNode.Category is WorkflowNodeCategory.Destination or WorkflowNodeCategory.Analytics)
        {
            return false;
        }

        return IsFhirDataContract(fromContract)
            && toCatalog.InputContracts.Any(IsFhirDataContract);
    }

    private static bool IsFhirDataContract(WorkflowDataContract contract)
        => contract is WorkflowDataContract.ResourceBatch
            or WorkflowDataContract.NormalizedResourceBatch
            or WorkflowDataContract.DeIdentifiedBatch;

    private static bool HasUpstreamPath(
        IReadOnlyDictionary<Guid, WorkflowNode> nodes,
        IReadOnlyCollection<WorkflowEdge> edges,
        Guid nodeId,
        Func<WorkflowNode, bool> predicate)
    {
        var incoming = edges.GroupBy(edge => edge.ToNodeId).ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(nodeId);

        while (stack.TryPop(out var currentNodeId))
        {
            if (!seen.Add(currentNodeId) || !incoming.TryGetValue(currentNodeId, out var upstreamEdges))
            {
                continue;
            }

            foreach (var edge in upstreamEdges)
            {
                var upstreamNode = nodes[edge.FromNodeId];
                if (predicate(upstreamNode))
                {
                    return true;
                }

                stack.Push(upstreamNode.Id);
            }
        }

        return false;
    }

    private static bool HasDownstreamPath(
        IReadOnlyDictionary<Guid, WorkflowNode> nodes,
        IReadOnlyCollection<WorkflowEdge> edges,
        Guid nodeId,
        Func<WorkflowNode, bool> predicate)
    {
        var outgoing = edges.GroupBy(edge => edge.FromNodeId).ToDictionary(group => group.Key, group => group.ToArray());
        var seen = new HashSet<Guid>();
        var stack = new Stack<Guid>();
        stack.Push(nodeId);

        while (stack.TryPop(out var currentNodeId))
        {
            if (!seen.Add(currentNodeId) || !outgoing.TryGetValue(currentNodeId, out var downstreamEdges))
            {
                continue;
            }

            foreach (var edge in downstreamEdges)
            {
                var downstreamNode = nodes[edge.ToNodeId];
                if (predicate(downstreamNode))
                {
                    return true;
                }

                stack.Push(downstreamNode.Id);
            }
        }

        return false;
    }

    private static bool HasCycle(IEnumerable<Guid> nodeIds, IReadOnlyCollection<WorkflowEdge> edges)
    {
        var nodeSet = nodeIds.ToHashSet();
        var outgoing = nodeSet.ToDictionary(id => id, _ => new List<Guid>());
        var indegree = nodeSet.ToDictionary(id => id, _ => 0);

        foreach (var edge in edges.Where(edge => nodeSet.Contains(edge.FromNodeId) && nodeSet.Contains(edge.ToNodeId)))
        {
            outgoing[edge.FromNodeId].Add(edge.ToNodeId);
            indegree[edge.ToNodeId]++;
        }

        var ready = new Queue<Guid>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;

        while (ready.TryDequeue(out var nodeId))
        {
            visited++;

            foreach (var downstreamNodeId in outgoing[nodeId])
            {
                indegree[downstreamNodeId]--;

                if (indegree[downstreamNodeId] == 0)
                {
                    ready.Enqueue(downstreamNodeId);
                }
            }
        }

        return visited != nodeSet.Count;
    }
}
