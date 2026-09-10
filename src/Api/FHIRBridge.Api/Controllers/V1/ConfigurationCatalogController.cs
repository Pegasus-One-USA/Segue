using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Read-only lists of the configured entities (source connections, destinations, mapping profiles) so the pipeline
/// builder can offer pickers that reference real, RBAC-scoped config by id (Option A). Creation/edit stays in
/// <see cref="ConfigurationsController"/>.
/// </summary>
/// <remarks>
/// No class-level policy: Source Connections, Destinations, and Mapping Profiles listing each use their
/// own dedicated View permission (see per-action attributes below) rather than a shared class-wide policy.
/// </remarks>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class ConfigurationCatalogController : ControllerBase
{
    private readonly IConfigurationRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;
    private readonly IAuthorizationService _authorizationService;

    public ConfigurationCatalogController(
        IConfigurationRepository repository,
        IConfigurationService configurationService,
        IUserDisplayNameResolver userDisplayNameResolver,
        IAuthorizationService authorizationService)
    {
        _repository = repository;
        _configurationService = configurationService;
        _userDisplayNameResolver = userDisplayNameResolver;
        _authorizationService = authorizationService;
    }

    [HttpGet("source-connections")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [ProducesResponseType(typeof(IReadOnlyList<SourceConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSourceConnections(CancellationToken cancellationToken)
    {
        var sources = await _repository.GetSourceConnectionsAsync(cancellationToken);
        var dtos = sources.Select(ConfigurationMapper.ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);

        return Ok(dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToArray());
    }

    [HttpGet("source-connections/paged")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [ProducesResponseType(typeof(PagedResult<SourceConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSourceConnectionsPaged(
        [FromQuery] string? search,
        [FromQuery] SourceSystemType? sourceSystemType,
        [FromQuery] ApplicationType? applicationType,
        [FromQuery] bool? isEnabled,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortOrder,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _repository.GetSourceConnectionsPagedAsync(
            new SourceConnectionFilter(search, sourceSystemType, applicationType, isEnabled),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            sortBy,
            sortOrder,
            cancellationToken);

        return Ok(new PagedResult<SourceConnectionDto>(
            result.Items.Select(ConfigurationMapper.ToDto).ToList(),
            result.TotalCount,
            result.Page,
            result.PageSize));
    }

    // Declares View for discovery purposes (see DynamicSourceSystemPermissionAttribute's remarks) — there's
    // no single-destination detail endpoint to attach a real per-type View check to (destinations are
    // otherwise only ever read back as part of a whole workflow definition, gated by workflow.view), so
    // this list is the one real place a per-type View check can mean something: which destination TYPES a
    // role sees rows for. The blanket DestinationConnections.View above still gates the endpoint itself.
    [HttpGet("destinations")]
    [StandardPermission(PermissionGroupCode.DestinationConnections, PermissionActionCode.View, description: "View the list of destination connections.")]
    [DynamicSourceSystemPermission(typeof(DestinationType), PermissionActionCode.View, description: "View destination connections of this type.")]
    [ProducesResponseType(typeof(IReadOnlyList<DestinationConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDestinations(CancellationToken cancellationToken)
    {
        var destinations = await _repository.GetDestinationsAsync(cancellationToken);
        var dtos = destinations.Select(ConfigurationMapper.ToDto).ToArray();

        var visibleTypes = new HashSet<DestinationType>();
        foreach (var type in dtos.Select(d => d.DestinationType).Distinct())
        {
            if (await ControllerAuthorizationExtensions.HasPermissionAsync(_authorizationService, User, type, PermissionActionCode.View))
            {
                visibleTypes.Add(type);
            }
        }
        dtos = dtos.Where(d => visibleTypes.Contains(d.DestinationType)).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);

        return Ok(dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToArray());
    }

    [HttpGet("destinations/paged")]
    [StandardPermission(PermissionGroupCode.DestinationConnections, PermissionActionCode.View, description: "View the list of destination connections.")]
    [ProducesResponseType(typeof(PagedResult<DestinationConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDestinationsPaged(
        [FromQuery] string? search,
        [FromQuery] DestinationType? destinationType,
        [FromQuery] bool? isEnabled,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortOrder,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService.GetDestinationConfigurationsPagedAsync(
            new DestinationFilter(search, destinationType, isEnabled),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            sortBy,
            sortOrder,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("destinations/{destinationId:guid}/has-execution-history")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(DestinationExecutionHistoryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> HasDestinationExecutionHistory(Guid destinationId, CancellationToken cancellationToken)
    {
        var hasHistory = await _configurationService.HasDestinationExecutionHistoryAsync(destinationId, cancellationToken);
        return Ok(new DestinationExecutionHistoryDto(hasHistory));
    }

    [HttpGet("mapping-profiles")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.View, description: "View the list of mapping profiles.")]
    [ProducesResponseType(typeof(IReadOnlyList<MappingProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMappingProfiles(CancellationToken cancellationToken)
    {
        var mappings = await _repository.GetMappingProfilesAsync(cancellationToken);
        var dtos = mappings.Select(ConfigurationMapper.ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);

        return Ok(dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToArray());
    }

    [HttpGet("mapping-profiles/paged")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.View, description: "View the list of mapping profiles.")]
    [ProducesResponseType(typeof(PagedResult<MappingProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMappingProfilesPaged(
        [FromQuery] string? search,
        [FromQuery] string? resourceType,
        [FromQuery] Guid? sourceConnectionId,
        [FromQuery] Guid? destinationId,
        [FromQuery] bool? isEnabled,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortOrder,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken)
    {
        var result = await _configurationService.GetMappingProfilesPagedAsync(
            new MappingProfileFilter(search, resourceType, sourceConnectionId, destinationId, isEnabled),
            page <= 0 ? 1 : page,
            pageSize <= 0 ? 25 : pageSize,
            sortBy,
            sortOrder,
            cancellationToken);

        return Ok(result);
    }

    [HttpGet("mapping-profiles/{mappingProfileId:guid}")]
    [StandardPermission(PermissionGroupCode.MappingProfiles, PermissionActionCode.View, description: "View a mapping profile.")]
    [ProducesResponseType(typeof(MappingProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMappingProfileById(Guid mappingProfileId, CancellationToken cancellationToken)
    {
        var mappingProfile = await _configurationService.GetMappingProfileByIdAsync(mappingProfileId, cancellationToken);
        return mappingProfile is null ? NotFound() : Ok(mappingProfile);
    }
}
