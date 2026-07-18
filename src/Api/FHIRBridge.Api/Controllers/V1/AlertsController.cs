using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>Alert Rules (CRUD) and Alert History — see IAlertEvaluationService for what actually fires them.</summary>
[ApiController]
[Authorize]
[Route("api/v1/governance")]
public sealed class AlertsController : ControllerBase
{
    private readonly IAlertRuleService _alertRuleService;
    private readonly ICurrentUserService _currentUserService;

    public AlertsController(IAlertRuleService alertRuleService, ICurrentUserService currentUserService)
    {
        _alertRuleService = alertRuleService;
        _currentUserService = currentUserService;
    }

    [HttpGet("alert-rules")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View alert rules.")]
    [ProducesResponseType(typeof(IReadOnlyList<AlertRuleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlertRules(CancellationToken cancellationToken)
        => Ok(await _alertRuleService.GetRulesAsync(cancellationToken));

    [HttpPost("alert-rules")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Create an alert rule.")]
    [ProducesResponseType(typeof(AlertRuleDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateAlertRule([FromBody] CreateAlertRuleRequest request, CancellationToken cancellationToken)
    {
        var created = await _alertRuleService.CreateRuleAsync(request, cancellationToken);
        return Created($"/api/v1/governance/alert-rules/{created.Id}", created);
    }

    [HttpPut("alert-rules/{id:guid}")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Edit an alert rule.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateAlertRule(Guid id, [FromBody] CreateAlertRuleRequest request, CancellationToken cancellationToken)
    {
        var updated = await _alertRuleService.UpdateRuleAsync(id, request, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpPost("alert-rules/{id:guid}/enabled")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Enable or disable an alert rule.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetAlertRuleEnabled(Guid id, [FromQuery] bool isEnabled, CancellationToken cancellationToken)
    {
        var updated = await _alertRuleService.SetRuleEnabledAsync(id, isEnabled, cancellationToken);
        return updated ? NoContent() : NotFound();
    }

    [HttpGet("alerts")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Read, description: "View alert history.")]
    [ProducesResponseType(typeof(IReadOnlyList<AlertHistoryDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAlertHistory([FromQuery] int take, CancellationToken cancellationToken)
        => Ok(await _alertRuleService.GetHistoryAsync(take, cancellationToken));

    [HttpPost("alerts/{id:guid}/acknowledge")]
    [StandardPermission(PermissionGroupCode.Governance, PermissionActionCode.Write, description: "Acknowledge a fired alert.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AcknowledgeAlert(Guid id, CancellationToken cancellationToken)
    {
        var acknowledgedBy = _currentUserService.CurrentUser.Email;
        var updated = await _alertRuleService.AcknowledgeAsync(id, acknowledgedBy, cancellationToken);
        return updated ? NoContent() : NotFound();
    }
}
