using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Application.Services.Workflows;

/// <summary>
/// Save-time rule: a destination may only write resource types its source reads. The source node declares the
/// types it reads (<see cref="WorkflowNodeResourceTypes.ReadSourceDeclared"/>); every type a destination selects or
/// maps must be among the declared types of the source node(s) that feed it through the canvas edges (their union
/// when several do). A destination can therefore never widen what its source fetches.
/// <para>
/// Checked when a workflow is saved, not in WorkflowGraphValidator (which also runs at run time) and not on copy,
/// so a workflow saved before this rule still runs unchanged. A source with no declared list (a legacy node: an EHR /
/// Generic FHIR node without the "Resource types declared" marker, whatever its "Resources" holds) skips the check
/// for every destination it feeds, and a destination with no resource types of its own (an analytics node, say) has
/// nothing to check. The portal applies the same rule before saving (upstream-source-v2.util.ts
/// findDestinationTypesOutsideSource) with the same decision and message.
/// </para>
/// </summary>
public static class WorkflowResourceTypeSubsetRule
{
    /// <summary>One canvas node, as the save endpoints receive it.</summary>
    public sealed record Node(
        string Id,
        string NodeType,
        WorkflowNodeCategory Category,
        string? DisplayName,
        string? ConfigurationJson);

    /// <summary>One canvas edge, by node id.</summary>
    public sealed record Edge(string FromNodeId, string ToNodeId);

    /// <summary>
    /// The first violation as a message naming the destination, its source(s) and the types, or null when every
    /// destination writes only what its source reads.
    /// </summary>
    public static string? Check(IReadOnlyCollection<Node> nodes, IReadOnlyCollection<Edge> edges)
    {
        var nodesById = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes)
        {
            nodesById.TryAdd(node.Id, node);
        }

        var incoming = edges
            .GroupBy(edge => edge.ToNodeId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(edge => edge.FromNodeId).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var destination in nodes.Where(node => node.Category == WorkflowNodeCategory.Destination))
        {
            var written = WorkflowNodeResourceTypes.ReadDestinationWritten(destination.ConfigurationJson);
            if (written.Count == 0)
            {
                continue;
            }

            var sources = UpstreamSources(destination.Id, nodesById, incoming);
            if (sources.Count == 0)
            {
                continue;
            }

            var read = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var legacySource = false;
            foreach (var source in sources)
            {
                var declared = WorkflowNodeResourceTypes.ReadSourceDeclared(source.NodeType, source.ConfigurationJson);
                if (declared is null)
                {
                    legacySource = true;
                    break;
                }

                read.UnionWith(declared);
            }

            if (legacySource)
            {
                continue;
            }

            var missing = written.Where(type => !read.Contains(type)).ToList();
            if (missing.Count > 0)
            {
                return Message(destination, sources, missing);
            }
        }

        return null;
    }

    // Walks the edges backwards from the destination through any chain nodes (mapping, transformation,
    // de-identification, ...) and stops at each source node it reaches.
    private static IReadOnlyList<Node> UpstreamSources(
        string destinationId,
        IReadOnlyDictionary<string, Node> nodesById,
        IReadOnlyDictionary<string, List<string>> incoming)
    {
        var sources = new List<Node>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { destinationId };
        var frontier = new Queue<string>();
        frontier.Enqueue(destinationId);
        while (frontier.Count > 0)
        {
            if (!incoming.TryGetValue(frontier.Dequeue(), out var predecessors))
            {
                continue;
            }

            foreach (var predecessorId in predecessors)
            {
                if (!visited.Add(predecessorId) || !nodesById.TryGetValue(predecessorId, out var predecessor))
                {
                    continue;
                }

                if (predecessor.Category == WorkflowNodeCategory.Source)
                {
                    sources.Add(predecessor);
                }
                else
                {
                    frontier.Enqueue(predecessorId);
                }
            }
        }

        return sources;
    }

    // Worded exactly as the portal's mirror of this rule (upstream-source-v2.util findDestinationTypesOutsideSource),
    // so the admin sees the same sentence whichever side catches it first.
    private static string Message(Node destination, IReadOnlyList<Node> sources, IReadOnlyList<string> missing)
    {
        var types = string.Join(", ", missing);
        var sourceNames = QuotedList(sources.Select(Name).ToList());
        var subject = sources.Count == 1 ? $"its source {sourceNames} does not" : $"its sources {sourceNames} do not";
        var pronoun = missing.Count == 1 ? "it" : "them";
        return $"Destination '{Name(destination)}' writes {types}, which {subject} read. " +
               $"Add {pronoun} to the source's resource types or remove {pronoun} from the destination.";
    }

    // 'A' / 'A' and 'B' / 'A', 'B' and 'C'.
    private static string QuotedList(IReadOnlyList<string> names)
    {
        var quoted = names.Select(name => $"'{name}'").ToList();
        return quoted.Count <= 1
            ? string.Concat(quoted)
            : $"{string.Join(", ", quoted.Take(quoted.Count - 1))} and {quoted[^1]}";
    }

    private static string Name(Node node) =>
        string.IsNullOrWhiteSpace(node.DisplayName) ? node.Id : node.DisplayName.Trim();
}
