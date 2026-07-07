namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// Scenario B feature flag. Gates whether an interactive/EHR launch executes the persisted workflow graph
/// (via <c>RankedWorkflowOrchestrator</c>) instead of the flat route path (<c>ConfiguredPipelineService</c>).
/// Defaults to OFF so the working route path is untouched until an operator opts in per source and verifies
/// the graph produces identical destination rows.
/// </summary>
public sealed class WorkflowGraphExecutionOptions
{
    public const string SectionName = "Workflow:GraphExecution";

    /// <summary>Master switch. When false, launches always use the route path.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional per-source allow-list. When <see cref="Enabled"/> is true and this is empty, the graph path
    /// applies to every source; when non-empty, only the listed source connections use the graph path.
    /// </summary>
    public Guid[] SourceConnectionIds { get; set; } = [];

    public bool IsEnabledForSource(Guid sourceConnectionId)
        => Enabled && (SourceConnectionIds.Length == 0 || Array.IndexOf(SourceConnectionIds, sourceConnectionId) >= 0);
}
