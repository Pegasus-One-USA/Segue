namespace FHIRBridge.Runtime.Domain.Workflows;

/// <summary>
/// What the workflow list shows in its Status column, and what the run endpoints gate on. See
/// <see cref="WorkflowDefinition.LifecycleStatus"/> for how it is decided.
///
/// Deliberately NOT a stored column: <see cref="Draft"/> vs <see cref="Ready"/> is a fact about the graph
/// (does it have a destination node), so persisting it would let the stored value drift away from the graph
/// it describes — a workflow whose only destination node was deleted would keep claiming to be Ready.
/// <see cref="Disabled"/> is the one state that IS stored, because "someone paused this" is a human decision
/// rather than something the graph can be asked about.
/// </summary>
public enum WorkflowLifecycleStatus
{
    /// <summary>No destination node yet — incomplete, and not runnable because it has nowhere to write.
    /// Still fully editable, and a source node can still be tested via its node checkpoint.</summary>
    Draft = 0,

    /// <summary>Has at least one destination node — will run. Reaching Ready locks nothing: adding
    /// transformation or de-identification afterwards is the normal path, not an exception.</summary>
    Ready = 1,

    /// <summary>Complete, but deliberately paused by an admin (<c>IsEnabled == false</c>). Outranks
    /// Draft/Ready in the UI because it is an explicit human decision about this workflow.</summary>
    Disabled = 2,
}
