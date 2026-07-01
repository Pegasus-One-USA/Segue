namespace FHIRBridge.Runtime.Application.Pipeline;

/// <summary>A node in the pipeline DAG: a named stage and the stages that must complete before it runs.</summary>
public sealed record PipelineStageDefinition(string Name, IReadOnlyCollection<string> DependsOn)
{
    public PipelineStageDefinition(string name, params string[] dependsOn)
        : this(name, (IReadOnlyCollection<string>)dependsOn)
    {
    }
}

/// <summary>
/// A directed acyclic graph of pipeline stages. Replaces the previously hard-coded Extract→Govern→Transform→Output
/// sequence with an explicit dependency graph: stages declare their prerequisites and the graph computes a valid
/// execution order, rejecting cycles and dangling dependencies. Branching is supported — a stage may depend on
/// several predecessors, and independent stages may share a dependency.
/// </summary>
public sealed class PipelineGraph
{
    private readonly IReadOnlyDictionary<string, PipelineStageDefinition> _stages;

    public PipelineGraph(IReadOnlyCollection<PipelineStageDefinition> stages)
    {
        if (stages is null || stages.Count == 0)
        {
            throw new ArgumentException("A pipeline graph requires at least one stage.", nameof(stages));
        }

        var byName = new Dictionary<string, PipelineStageDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var stage in stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Name))
            {
                throw new ArgumentException("Pipeline stage name cannot be empty.", nameof(stages));
            }

            if (!byName.TryAdd(stage.Name, stage))
            {
                throw new ArgumentException($"Duplicate pipeline stage '{stage.Name}'.", nameof(stages));
            }
        }

        foreach (var stage in byName.Values)
        {
            foreach (var dependency in stage.DependsOn)
            {
                if (!byName.ContainsKey(dependency))
                {
                    throw new InvalidOperationException(
                        $"Pipeline stage '{stage.Name}' depends on unknown stage '{dependency}'.");
                }
            }
        }

        _stages = byName;
        ExecutionOrder = ComputeTopologicalOrder(byName);
    }

    public IReadOnlyList<string> Stages => _stages.Keys.ToList();

    /// <summary>The validated topological execution order (dependencies always precede dependents).</summary>
    public IReadOnlyList<string> ExecutionOrder { get; }

    private static IReadOnlyList<string> ComputeTopologicalOrder(
        IReadOnlyDictionary<string, PipelineStageDefinition> stages)
    {
        var order = new List<string>(stages.Count);
        var state = new Dictionary<string, VisitState>(StringComparer.OrdinalIgnoreCase);

        void Visit(string name, Stack<string> path)
        {
            if (state.TryGetValue(name, out var visitState))
            {
                if (visitState == VisitState.Visiting)
                {
                    var cycle = string.Join(" -> ", path.Reverse().Append(name));
                    throw new InvalidOperationException($"Pipeline graph contains a cycle: {cycle}.");
                }

                return; // already fully visited
            }

            state[name] = VisitState.Visiting;
            path.Push(name);

            foreach (var dependency in stages[name].DependsOn)
            {
                Visit(dependency, path);
            }

            path.Pop();
            state[name] = VisitState.Visited;
            order.Add(name);
        }

        // Deterministic iteration so the order is stable across runs.
        foreach (var name in stages.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            Visit(name, new Stack<string>());
        }

        return order;
    }

    private enum VisitState
    {
        Visiting,
        Visited
    }
}
