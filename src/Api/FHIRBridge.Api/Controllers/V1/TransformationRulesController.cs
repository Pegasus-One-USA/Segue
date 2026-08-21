using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.DTOs.Transforms;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Transforms;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// CRUD for scoped transform rules (Global/DestinationType/ResourceType/Field/Workflow) plus a resolve-and-apply
/// preview endpoint — the backend for the destination wizard's "Rules" button and modal.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/transformation-rules")]
public sealed class TransformationRulesController : ControllerBase
{
    private readonly ITransformationRuleService _service;
    private readonly ISystemSettingsCache _settingsCache;

    public TransformationRulesController(ITransformationRuleService service, ISystemSettingsCache settingsCache)
    {
        _service = service;
        _settingsCache = settingsCache;
    }

    /// <summary>Whether the whole feature is currently hidden (Settings &gt; System Settings &gt; General,
    /// key "TransformationRules:Hidden", default false). Deliberately requires only <see cref="Authorize"/> at
    /// the class level — not UnifiedAdmin — since any authenticated portal user building a workflow needs to
    /// know whether to show the Rules button, not just admins.</summary>
    [HttpGet("hidden")]
    [ProducesResponseType(typeof(TransformationRulesHiddenDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> IsHidden(CancellationToken cancellationToken)
    {
        var hidden = await _settingsCache.GetBoolAsync(
            TransformationRulesFeatureFlag.SettingKey, TransformationRulesFeatureFlag.DefaultHidden, cancellationToken);
        return Ok(new TransformationRulesHiddenDto(hidden));
    }

    /// <summary>Every rule configured for the given filters, across whichever scopes match — the Rules modal
    /// uses this (unfiltered by scope) to show what's already defined at every tier for one resource type.</summary>
    [HttpGet]
    [StandardPermission(PermissionGroupCode.TransformationRules, PermissionActionCode.View, description: "View transformation rules.")]
    [ProducesResponseType(typeof(List<TransformationRuleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRules(
        [FromQuery] TransformScope? scope,
        [FromQuery] DestinationType? destinationType,
        [FromQuery] string? resourceType,
        [FromQuery] string? destinationField,
        [FromQuery] Guid? resourcePipelineRouteId,
        [FromQuery] string? sourceSystem,
        [FromQuery] string? sourceField,
        CancellationToken cancellationToken)
    {
        var rules = await _service.ListRulesAsync(
            scope, destinationType, resourceType, destinationField, resourcePipelineRouteId, sourceSystem, sourceField, cancellationToken);
        return Ok(rules);
    }

    /// <summary>Config schema for every node type — which keys it reads, what control to render, and its
    /// default — so the UI can render proper dropdowns/checkboxes instead of a raw JSON textarea.</summary>
    [HttpGet("node-schemas")]
    [StandardPermission(PermissionGroupCode.TransformationRules, PermissionActionCode.View, description: "View transformation rules.")]
    [ProducesResponseType(typeof(List<TransformNodeSchemaDto>), StatusCodes.Status200OK)]
    public IActionResult GetNodeSchemas() => Ok(_service.GetNodeSchemas());

    /// <summary>Creates a new rule, or updates one in place when <see cref="SaveTransformationRuleRequest.Id"/> is supplied.</summary>
    [HttpPost]
    [StandardPermission(
        PermissionGroupCode.TransformationRules,
        PermissionActionCode.Write,
        description: "Create or update a scoped transformation rule.")]
    [ProducesResponseType(typeof(TransformationRuleDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveRule(
        [FromBody] SaveTransformationRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _service.SaveRuleAsync(request, cancellationToken);
        return Ok(rule);
    }

    [HttpDelete("{ruleId:guid}")]
    [StandardPermission(
        PermissionGroupCode.TransformationRules,
        PermissionActionCode.Delete,
        description: "Delete a scoped transformation rule.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteRule(Guid ruleId, CancellationToken cancellationToken)
    {
        await _service.DeleteRuleAsync(ruleId, cancellationToken);
        return NoContent();
    }

    /// <summary>Resolves whichever rule chain currently applies to one destination field and runs a sample
    /// value through it, returning the final value plus a per-node trace — the modal's live preview and the
    /// wizard's "auto-applied on add" pre-fill both call this.</summary>
    [HttpPost("preview")]
    [StandardPermission(PermissionGroupCode.TransformationRules, PermissionActionCode.View, description: "View transformation rules.")]
    [ProducesResponseType(typeof(TransformPreviewResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(
        [FromBody] TransformPreviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _service.PreviewAsync(request, cancellationToken);
        return Ok(result);
    }

    /// <summary>The actual rule row(s) currently in effect for one field (id, full config, scope) — used to
    /// show/clone what's really running when there's no Field-level rule of its own yet, instead of the wizard's
    /// "Add rule" starting from blank schema defaults.</summary>
    [HttpGet("effective")]
    [StandardPermission(PermissionGroupCode.TransformationRules, PermissionActionCode.View, description: "View transformation rules.")]
    [ProducesResponseType(typeof(List<TransformationRuleDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetEffectiveRules(
        [FromQuery] DestinationType destinationType,
        [FromQuery] string resourceType,
        [FromQuery] string destinationField,
        [FromQuery] Guid? resourcePipelineRouteId,
        [FromQuery] string? sourceSystem,
        [FromQuery] string? sourceField,
        CancellationToken cancellationToken)
    {
        var rules = await _service.GetEffectiveRulesAsync(
            destinationType, resourceType, destinationField, resourcePipelineRouteId, sourceSystem, sourceField, cancellationToken);
        return Ok(rules);
    }
}
