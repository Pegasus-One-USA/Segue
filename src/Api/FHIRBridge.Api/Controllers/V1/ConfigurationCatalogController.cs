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
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1")]
public sealed class ConfigurationCatalogController : ControllerBase
{
    private readonly IConfigurationRepository _repository;
    private readonly IConfigurationService _configurationService;
    private readonly IUserDisplayNameResolver _userDisplayNameResolver;

    public ConfigurationCatalogController(
        IConfigurationRepository repository,
        IConfigurationService configurationService,
        IUserDisplayNameResolver userDisplayNameResolver)
    {
        _repository = repository;
        _configurationService = configurationService;
        _userDisplayNameResolver = userDisplayNameResolver;
    }

    [HttpGet("source-connections")]
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

    [HttpGet("destinations")]
    [ProducesResponseType(typeof(IReadOnlyList<DestinationConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDestinations(CancellationToken cancellationToken)
    {
        var destinations = await _repository.GetDestinationsAsync(cancellationToken);
        var dtos = destinations.Select(ConfigurationMapper.ToDto).ToArray();

        var names = await _userDisplayNameResolver.ResolveAsync(
            dtos.SelectMany(dto => new[] { dto.CreatedBy, dto.ModifiedBy }), cancellationToken);

        return Ok(dtos.Select(dto => dto with
        {
            CreatedBy = dto.CreatedBy is { } createdBy ? names.GetValueOrDefault(createdBy, createdBy) : null,
            ModifiedBy = dto.ModifiedBy is { } modifiedBy ? names.GetValueOrDefault(modifiedBy, modifiedBy) : null,
        }).ToArray());
    }

    [HttpGet("destinations/paged")]
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
    [ProducesResponseType(typeof(DestinationExecutionHistoryDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> HasDestinationExecutionHistory(Guid destinationId, CancellationToken cancellationToken)
    {
        var hasHistory = await _configurationService.HasDestinationExecutionHistoryAsync(destinationId, cancellationToken);
        return Ok(new DestinationExecutionHistoryDto(hasHistory));
    }

    [HttpGet("mapping-profiles")]
    [ProducesResponseType(typeof(IReadOnlyList<MappingProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMappingProfiles(CancellationToken cancellationToken)
    {
        var mappings = await _repository.GetMappingProfilesAsync(cancellationToken);
        return Ok(mappings.Select(ConfigurationMapper.ToDto).ToArray());
    }
}
