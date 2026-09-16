namespace FHIRBridge.Application.Services.Workflows;

/// <summary>
/// Converts stored V1 graphs to the V2 shape, so every workflow still opens once V1's builder is deleted.
/// See docs/backend/18-workflow-self-contained-config-plan.md §8.4.
/// </summary>
public interface IWorkflowGraphVersionMigrationService
{
    /// <param name="dryRun">
    /// True (the default) reports what WOULD change and writes nothing. This conversion collapses several
    /// nodes into one, which changes what the graph does — it must be reviewed before it is applied.
    /// </param>
    Task<WorkflowGraphVersionMigrationResult> ConvertAsync(
        bool dryRun,
        Guid? workflowId,
        CancellationToken cancellationToken);
}

/// <summary>What conversion would do, or did, to one workflow.</summary>
public sealed record WorkflowGraphVersionReport(
    Guid WorkflowId,
    string Name,
    bool RequiresConversion,
    IReadOnlyList<string> CollapsedNodes,
    string? Blocker = null);

public sealed record WorkflowGraphVersionMigrationResult(
    bool DryRun,
    IReadOnlyList<WorkflowGraphVersionReport> Workflows,
    int WorkflowsWritten)
{
    public int RequiringConversionCount => Workflows.Count(workflow => workflow.RequiresConversion);

    public int BlockedCount => Workflows.Count(workflow => workflow.Blocker is not null);

    /// <summary>True when every V1 graph either converted or needs no conversion — the condition plan §8.5
    /// requires before V1's builder may be deleted.</summary>
    public bool IsSafeToRetireV1 => BlockedCount == 0;
}
