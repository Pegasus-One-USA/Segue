using System.Text.Json;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// De-tenanted configuration CRUD. Successor to <c>TenantConfigurationsController</c> with the tenant route segment
/// removed. Source-connection testing, capability discovery, destination schema, pipeline runs, and user access live
/// in their own controllers.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class ConfigurationsController : ControllerBase
{
    private readonly IConfigurationService _configurationService;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IAuthorizationService _authorizationService;
    private readonly IEpicSourceConnectionScopeSyncService _scopeSyncService;
    private readonly IMappingImportService _mappingImportService;

    public ConfigurationsController(
        IConfigurationService configurationService,
        IConfigurationRepository configurationRepository,
        IAuthorizationService authorizationService,
        IEpicSourceConnectionScopeSyncService scopeSyncService,
        IMappingImportService mappingImportService)
    {
        _configurationService = configurationService;
        _configurationRepository = configurationRepository;
        _authorizationService = authorizationService;
        _scopeSyncService = scopeSyncService;
        _mappingImportService = mappingImportService;
    }

    // ── Source connections ────────────────────────────────────────────────────
    // Which permission a source connection requires depends on its vendor (SourceSystemType), known
    // only once the request is inspected — a static [StandardPermission] can't express that, so these
    // two check a resolved, vendor-specific permission at runtime instead of declaring one up front.
    // Add/Update used to share one combined Edit permission per vendor; now split into the vendor's own
    // Create (this endpoint always mints a new SourceConnection) vs. Edit (UpdateSourceConnection below),
    // matching the same node-level View/Create/Edit/Delete/Execute split as every other vendor action.

    // The Execute declaration below is discovery-only (no [DynamicSourceSystemPermission] site
    // enforces it here) — WorkflowEndpoints.cs's /workflows/{id}/run is a minimal-API endpoint, not a
    // ControllerBase action, so it can't itself carry an attribute PermissionCatalog would scan; this is
    // simply where "epic.execute" etc. get catalogued so the Role Permissions screen can manage them,
    // while the actual check happens in /run. For Epic/Athenahealth/Cerner specifically, this crosses
    // the same already-hand-seeded permission RbacSeedData created for exactly this purpose ("Trigger a
    // pipeline run against ...") — same deterministic id, so this only ever updates that existing row's
    // metadata, never creates a duplicate.
    [HttpPost("source-connections")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Create, description: "Create a source connection.")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Execute, description: "Execute a workflow using this source connection's vendor.")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddSourceConnection(
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await this.AuthorizePermissionAsync(_authorizationService, request.SourceSystemType, PermissionActionCode.Create);
        if (denied is not null) return denied;

        var sourceConnection = await _configurationService.AddSourceConnectionAsync(request, cancellationToken);

        return Created($"/api/v1/source-connections/{sourceConnection.Id}", sourceConnection);
    }

    [HttpPut("source-connections/{sourceConnectionId:guid}")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Edit, description: "Add or edit a source connection.")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSourceConnection(
        Guid sourceConnectionId,
        [FromBody] CreateSourceConnectionRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await this.AuthorizePermissionAsync(_authorizationService, request.SourceSystemType, PermissionActionCode.Edit);
        if (denied is not null) return denied;

        var sourceConnection = await _configurationService.UpdateSourceConnectionAsync(
            sourceConnectionId,
            request,
            cancellationToken);

        return Ok(sourceConnection);
    }

    // Deactivating only takes an id, not the connection's vendor — resolving that would need a lookup
    // first (the write-only IConfigurationService doesn't expose one yet), so this stays admin-only for
    // now rather than guess. Add a GetSourceConnectionByIdAsync + the same AuthorizePermissionAsync call
    // once that's needed.
    [HttpPost("source-connections/{sourceConnectionId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateSourceConnection(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.SetSourceConnectionEnabledAsync(
            sourceConnectionId,
            false,
            cancellationToken);

        return Ok(sourceConnection);
    }

    /// <summary>
    /// Recomputes one source connection's OAuth scopes from what its pipelines actually consume right now (the
    /// union of every destination's selected resource types, across every workflow referencing it) and persists the
    /// result. A no-op (returns null scopes) for non-interactive (Backend Services) connections. Use this to force a
    /// resync without waiting for the next workflow save that references the connection.
    /// </summary>
    [HttpPost("source-connections/{sourceConnectionId:guid}/sync-scopes")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SyncSourceConnectionScopes(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var scopes = await _scopeSyncService.SyncAsync(sourceConnectionId, cancellationToken);
        return Ok(new { sourceConnectionId, scopes });
    }

    /// <summary>
    /// One-time (or as-needed) backfill: resyncs every interactive source connection's scopes to match actual
    /// pipeline usage. Intended for correcting connections that drifted under the old behavior (each source node's
    /// own resource selection overwriting the shared connection on save, independent of what other pipelines
    /// sharing that connection actually need) before this sync-on-save behavior existed. Safe to re-run.
    /// </summary>
    [HttpPost("source-connections/scopes/sync-all")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SyncAllSourceConnectionScopes(CancellationToken cancellationToken)
    {
        var changedConnectionIds = await _scopeSyncService.SyncAllAsync(cancellationToken);
        return Ok(new { changedConnectionIds });
    }

    // ── Webhooks ──────────────────────────────────────────────────────────────

    [HttpPost("webhooks")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddWebhookConfiguration(
        [FromBody] CreateWebhookConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _configurationService.AddWebhookConfigurationAsync(request, cancellationToken);

        return Created($"/api/v1/webhooks/{webhookConfiguration.Id}", webhookConfiguration);
    }

    [HttpPost("webhooks/{webhookConfigurationId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(WebhookConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateWebhookConfiguration(
        Guid webhookConfigurationId,
        CancellationToken cancellationToken)
    {
        var webhookConfiguration = await _configurationService.SetWebhookConfigurationEnabledAsync(
            webhookConfigurationId,
            false,
            cancellationToken);

        return Ok(webhookConfiguration);
    }

    // ── Destinations ──────────────────────────────────────────────────────────

    // Which permission a destination configuration requires depends on its type (DestinationType), known only
    // once the request is inspected — same reasoning as the source-connection pair above, and the same
    // DynamicSourceSystemPermission/AuthorizePermissionAsync mechanism, just crossed with DestinationType
    // instead of SourceSystemType (see SourceSystemPermissionGroups — already generic over either enum).
    // Add/Update used to share one combined Edit permission per type; now split into Create (this endpoint
    // always mints a new DestinationConfiguration) vs. Edit (UpdateDestinationConfiguration below).

    // Execute is discovery-only here too — see the matching comment on AddSourceConnection above; the
    // real check lives in WorkflowEndpoints.cs's /workflows/{id}/run.
    [HttpPost("destinations")]
    [DynamicSourceSystemPermission(typeof(DestinationType), PermissionActionCode.Create, description: "Create a destination configuration.")]
    [DynamicSourceSystemPermission(typeof(DestinationType), PermissionActionCode.Execute, description: "Execute a workflow using this destination type.")]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddDestinationConfiguration(
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await this.AuthorizePermissionAsync(_authorizationService, request.DestinationType, PermissionActionCode.Create);
        if (denied is not null) return denied;

        var destinationConfiguration = await _configurationService.AddDestinationConfigurationAsync(request, cancellationToken);

        return Created($"/api/v1/destinations/{destinationConfiguration.Id}", destinationConfiguration);
    }

    [HttpPut("destinations/{destinationId:guid}")]
    [DynamicSourceSystemPermission(typeof(DestinationType), PermissionActionCode.Edit, description: "Add or edit a destination configuration.")]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateDestinationConfiguration(
        Guid destinationId,
        [FromBody] CreateDestinationConfigurationRequest request,
        CancellationToken cancellationToken)
    {
        var denied = await this.AuthorizePermissionAsync(_authorizationService, request.DestinationType, PermissionActionCode.Edit);
        if (denied is not null) return denied;

        var destinationConfiguration = await _configurationService.UpdateDestinationConfigurationAsync(
            destinationId,
            request,
            cancellationToken);

        return Ok(destinationConfiguration);
    }

    [HttpPost("destinations/{destinationId:guid}/deactivate")]
    [StandardPermission(PermissionGroupCode.DestinationConnections, PermissionActionCode.Deactivate, description: "Deactivate a destination connection.")]
    [ProducesResponseType(typeof(DestinationConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateDestinationConfiguration(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        var destinationConfiguration = await _configurationService.SetDestinationConfigurationEnabledAsync(
            destinationId,
            false,
            cancellationToken);

        return Ok(destinationConfiguration);
    }

    [HttpDelete("destinations/{destinationId:guid}")]
    [StandardPermission(PermissionGroupCode.DestinationConnections, PermissionActionCode.Delete, description: "Delete a destination connection.")]
    [DynamicSourceSystemPermission(typeof(DestinationType), PermissionActionCode.Delete, description: "Delete a destination configuration.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteDestinationConfiguration(
        Guid destinationId,
        CancellationToken cancellationToken)
    {
        // Two independent layers, same as everywhere else in this file: the generic
        // DestinationConnections.Delete above (already enforced by [StandardPermission]) AND this
        // type-specific one — a role needs both the module-level Delete permission and this
        // particular destination's own type's Delete permission. The type isn't in the request (just
        // an id), so it's resolved by fetching the row first, same pattern /workflows/{id}/copy uses.
        var destination = await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
        if (destination is null)
        {
            return NotFound();
        }

        var denied = await this.AuthorizePermissionAsync(_authorizationService, destination.DestinationType, PermissionActionCode.Delete);
        if (denied is not null) return denied;

        await _configurationService.DeleteDestinationConfigurationAsync(destinationId, cancellationToken);
        return NoContent();
    }

    // ── Mapping profiles ──────────────────────────────────────────────────────

    [HttpPost("mapping-profiles")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.Create, description: "Create a mapping profile.")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddMappingProfile(
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.AddMappingProfileAsync(request, cancellationToken);

        return Created($"/api/v1/mapping-profiles/{mappingProfile.Id}", mappingProfile);
    }

    [HttpPut("mapping-profiles/{mappingProfileId:guid}")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.Edit, description: "Edit a mapping profile.")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateMappingProfile(
        Guid mappingProfileId,
        [FromBody] CreateMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.UpdateMappingProfileAsync(
            mappingProfileId,
            request,
            cancellationToken);

        return Ok(mappingProfile);
    }

    [HttpPost("mapping-profiles/{mappingProfileId:guid}/deactivate")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.Deactivate, description: "Activate or deactivate a mapping profile.")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateMappingProfile(
        Guid mappingProfileId,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.SetMappingProfileEnabledAsync(
            mappingProfileId,
            false,
            cancellationToken);

        return Ok(mappingProfile);
    }

    [HttpPost("mapping-profiles/{mappingProfileId:guid}/activate")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.Deactivate, description: "Activate or deactivate a mapping profile.")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ActivateMappingProfile(
        Guid mappingProfileId,
        CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.SetMappingProfileEnabledAsync(
            mappingProfileId,
            true,
            cancellationToken);

        return Ok(mappingProfile);
    }

    [HttpDelete("mapping-profiles/{mappingProfileId:guid}")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.Delete, description: "Delete a mapping profile.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteMappingProfile(
        Guid mappingProfileId,
        CancellationToken cancellationToken)
    {
        var usageCount = await _configurationService.GetMappingProfileUsageCountAsync(mappingProfileId, cancellationToken);
        if (usageCount > 0)
        {
            return Conflict(new
            {
                message = $"This mapping profile is used by {usageCount} pipeline route(s) and cannot be deleted.",
            });
        }

        await _configurationService.DeleteMappingProfileAsync(mappingProfileId, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Promotes a workflow's own mapping profile into a new, independently-named master template that other
    /// workflows can find via "Select Existing" and clone from. Always creates a brand-new mapping profile —
    /// never overwrites an existing one, even one that already matches the same resource type/source/destination.
    /// </summary>
    [HttpPost("mapping-profiles/{mappingProfileId:guid}/promote-to-master")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.Create, description: "Promote a mapping profile to a reusable master template (always creates a new profile).")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> PromoteMappingProfileToMaster(
        Guid mappingProfileId,
        [FromBody] PromoteMappingProfileRequest request,
        CancellationToken cancellationToken)
    {
        var masterProfile = await _configurationService.PromoteMappingProfileToMasterAsync(
            mappingProfileId, request.Name, cancellationToken);

        return Created($"/api/v1/mapping-profiles/{masterProfile.Id}", masterProfile);
    }

    /// <summary>
    /// Imports the field-mapping configuration produced by the Workflow Builder's "Update" button: persists
    /// one mapping profile per resourceType and applies the destination schema changes (new tables/columns)
    /// it implies. Safely re-runnable — re-posting the same payload reports everything as already-existing
    /// rather than duplicating profiles/tables/columns.
    /// </summary>
    [HttpPost("mapping-profiles/import")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [StandardPermission(
        PermissionGroupCode.Configuration,
        PermissionActionCode.Write,
        description: "Import a mapping configuration and apply destination schema changes.")]
    [ProducesResponseType(typeof(MappingImportResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> ImportMappingConfiguration(
        [FromBody] JsonElement request,
        CancellationToken cancellationToken)
        => Ok(await _mappingImportService.ImportAsync(request, cancellationToken));

    // ── Resources / routes ────────────────────────────────────────────────────

    [HttpPost("resources")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> ConfigureResource(
        [FromBody] ConfigureResourceRequest request,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _configurationService.ConfigureResourceAsync(request, cancellationToken);

        return Created($"/api/v1/resources/{resourceConfiguration.ResourceType}", resourceConfiguration);
    }

    [HttpPost("resources/{resourceType}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourceConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateResourceConfiguration(
        string resourceType,
        CancellationToken cancellationToken)
    {
        var resourceConfiguration = await _configurationService.SetResourceConfigurationEnabledAsync(
            resourceType,
            false,
            cancellationToken);

        return Ok(resourceConfiguration);
    }

    [HttpPost("resources/{resourceType}/routes")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> AddResourceRoute(
        string resourceType,
        [FromBody] CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await _configurationService.AddResourceRouteAsync(resourceType, request, cancellationToken);

        return Created($"/api/v1/resources/{resourceType}/routes/{route.Id}", route);
    }

    [HttpPut("resources/{resourceType}/routes/{routeId:guid}")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateResourceRoute(
        string resourceType,
        Guid routeId,
        [FromBody] CreateResourceRouteRequest request,
        CancellationToken cancellationToken)
    {
        var route = await _configurationService.UpdateResourceRouteAsync(resourceType, routeId, request, cancellationToken);

        return Ok(route);
    }

    [HttpPost("resources/{resourceType}/routes/{routeId:guid}/deactivate")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(ResourcePipelineRouteDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DeactivateResourceRoute(
        string resourceType,
        Guid routeId,
        CancellationToken cancellationToken)
    {
        var route = await _configurationService.SetResourceRouteEnabledAsync(resourceType, routeId, false, cancellationToken);

        return Ok(route);
    }
}
