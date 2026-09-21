namespace FHIRBridge.Application.Services.Workflows;

/// <summary>
/// What the migration would do, or did, to one node. <see cref="Blocker"/> being non-null means this node was
/// NOT written: an id that does not resolve is exactly the corruption the self-contained model exists to
/// prevent, so it is reported rather than inlined as an empty snapshot.
/// See docs/backend/18-workflow-self-contained-config-plan.md §7.
/// </summary>
public sealed record WorkflowNodeMigrationReport(
    Guid NodeId,
    string NodeType,
    string DisplayName,
    WorkflowNodeMigrationAction Action,
    IReadOnlyList<string> ResolvedReferences,
    string? Blocker = null);

public enum WorkflowNodeMigrationAction
{
    /// <summary>Already enveloped — nothing to do. Makes the migration re-runnable.</summary>
    AlreadyMigrated = 0,

    /// <summary>Would be (or was) rewritten into the enveloped shape.</summary>
    Migrate = 1,

    /// <summary>Carries no master ids at all (a transform or compliance node), so it is enveloped as-is.</summary>
    EnvelopeOnly = 2,

    /// <summary>Left untouched — see <see cref="WorkflowNodeMigrationReport.Blocker"/>.</summary>
    Blocked = 3,
}

/// <summary>One workflow's nodes. <see cref="IsBlocked"/> is all-or-nothing on purpose: a half-migrated graph
/// (some nodes enveloped, one still pointing at a master that has gone) is worse than an untouched one.</summary>
public sealed record WorkflowMigrationReport(
    Guid WorkflowId,
    string Name,
    IReadOnlyList<WorkflowNodeMigrationReport> Nodes)
{
    public bool IsBlocked => Nodes.Any(node => node.Action == WorkflowNodeMigrationAction.Blocked);

    public int MigratableNodeCount =>
        Nodes.Count(node => node.Action is WorkflowNodeMigrationAction.Migrate or WorkflowNodeMigrationAction.EnvelopeOnly);
}

/// <summary>
/// The whole run. In dry-run mode nothing is written and <see cref="WorkflowsWritten"/> is zero — the report is
/// the deliverable, and is meant to be read before the same call is made with <c>dryRun: false</c>.
/// </summary>
public sealed record WorkflowConfigurationMigrationResult(
    bool DryRun,
    IReadOnlyList<WorkflowMigrationReport> Workflows,
    int WorkflowsWritten,
    IReadOnlyList<string> OrphanTransformationRules)
{
    public int BlockedWorkflowCount => Workflows.Count(workflow => workflow.IsBlocked);

    public int AlreadyMigratedWorkflowCount => Workflows.Count(workflow =>
        workflow.Nodes.Count > 0
        && workflow.Nodes.All(node => node.Action == WorkflowNodeMigrationAction.AlreadyMigrated));
}
