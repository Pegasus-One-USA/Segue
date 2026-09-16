using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Workflows;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Operator tooling for the node-configuration migration (see
/// docs/backend/18-workflow-self-contained-config-plan.md §7): rewrites stored workflow nodes from the legacy
/// flat shape into the enveloped shape.
///
/// Dry-run is the default and apply must be asked for explicitly, because a node whose ids no longer resolve is
/// exactly the corruption this work exists to prevent — it has to be seen and decided on, never silently written
/// as an empty snapshot. Admin-gated: this rewrites every stored workflow.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/workflows/configuration-migration")]
public sealed class WorkflowConfigurationMigrationController : ControllerBase
{
    private readonly IWorkflowConfigurationMigrationService _migrationService;
    private readonly IWorkflowGraphVersionMigrationService _graphVersionMigrationService;

    public WorkflowConfigurationMigrationController(
        IWorkflowConfigurationMigrationService migrationService,
        IWorkflowGraphVersionMigrationService graphVersionMigrationService)
    {
        _migrationService = migrationService;
        _graphVersionMigrationService = graphVersionMigrationService;
    }

    /// <summary>Reports what the migration would do. Writes nothing.</summary>
    [HttpGet("dry-run")]
    public async Task<ActionResult<WorkflowConfigurationMigrationResult>> DryRunAsync(
        [FromQuery] Guid? workflowId,
        CancellationToken cancellationToken)
        => Ok(await _migrationService.MigrateAsync(dryRun: true, workflowId, cancellationToken));

    /// <summary>
    /// Applies the migration. <paramref name="confirm"/> must be true — the extra step is deliberate, so this
    /// cannot be reached by an idle POST from a browser or a mis-scripted call.
    /// </summary>
    [HttpPost("apply")]
    public async Task<ActionResult<WorkflowConfigurationMigrationResult>> ApplyAsync(
        [FromQuery] bool confirm,
        [FromQuery] Guid? workflowId,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            return BadRequest(new
            {
                message = "Pass confirm=true to apply. Run the dry-run first and review its report — "
                    + "blocked workflows are left untouched and need a decision before they can migrate.",
            });
        }

        return Ok(await _migrationService.MigrateAsync(dryRun: false, workflowId, cancellationToken));
    }

    /// <summary>
    /// Reports which stored graphs still use V1-only node types, and what converting them to V2 would collapse.
    /// Writes nothing. Run this before retiring V1's builder: IsSafeToRetireV1 on the result is the check plan
    /// §8.5 requires, since deleting the builder first would leave those workflows unopenable.
    /// </summary>
    [HttpGet("graph-version/dry-run")]
    public async Task<ActionResult<WorkflowGraphVersionMigrationResult>> GraphVersionDryRunAsync(
        [FromQuery] Guid? workflowId,
        CancellationToken cancellationToken)
        => Ok(await _graphVersionMigrationService.ConvertAsync(dryRun: true, workflowId, cancellationToken));

    /// <summary>
    /// Converts V1 graphs to the V2 shape. This COLLAPSES several granular transform steps into one, which
    /// changes what the graph does — review the dry-run first. Workflows the converter cannot handle cleanly
    /// are reported and left untouched.
    /// </summary>
    [HttpPost("graph-version/apply")]
    public async Task<ActionResult<WorkflowGraphVersionMigrationResult>> GraphVersionApplyAsync(
        [FromQuery] bool confirm,
        [FromQuery] Guid? workflowId,
        CancellationToken cancellationToken)
    {
        if (!confirm)
        {
            return BadRequest(new
            {
                message = "Pass confirm=true to apply. This collapses V1's granular transform steps "
                    + "(normalize, terminology, patient matching, ...) into a single Transformation node — "
                    + "review the dry-run report before running it.",
            });
        }

        return Ok(await _graphVersionMigrationService.ConvertAsync(dryRun: false, workflowId, cancellationToken));
    }
}
