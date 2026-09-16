namespace FHIRBridge.Application.Services.Workflows;

/// <summary>
/// Rewrites stored workflow nodes from the legacy flat shape (settings at the root, alongside ids pointing at
/// master records) into the enveloped shape (settings under <c>config</c>, provenance under <c>ref</c>).
/// See docs/backend/18-workflow-self-contained-config-plan.md §7.
/// </summary>
public interface IWorkflowConfigurationMigrationService
{
    /// <param name="dryRun">
    /// True (the default at every call site that is not an explicit, reviewed apply) reports what WOULD happen
    /// and writes nothing. The plan makes this non-negotiable: a node whose ids no longer resolve must be seen
    /// and decided on, never silently written as an empty snapshot.
    /// </param>
    /// <param name="workflowId">One workflow, or null for every stored workflow.</param>
    Task<WorkflowConfigurationMigrationResult> MigrateAsync(
        bool dryRun,
        Guid? workflowId,
        CancellationToken cancellationToken);
}
