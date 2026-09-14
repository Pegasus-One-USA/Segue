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

    public WorkflowConfigurationMigrationController(IWorkflowConfigurationMigrationService migrationService)
    {
        _migrationService = migrationService;
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
}
