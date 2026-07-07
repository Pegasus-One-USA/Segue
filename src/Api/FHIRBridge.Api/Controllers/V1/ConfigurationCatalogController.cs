using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Security;
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

    public ConfigurationCatalogController(IConfigurationRepository repository)
    {
        _repository = repository;
    }

    [HttpGet("source-connections")]
    [ProducesResponseType(typeof(IReadOnlyList<SourceConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSourceConnections(CancellationToken cancellationToken)
    {
        var sources = await _repository.GetSourceConnectionsAsync(cancellationToken);
        return Ok(sources.Select(ConfigurationMapper.ToDto).ToArray());
    }

    [HttpGet("destinations")]
    [ProducesResponseType(typeof(IReadOnlyList<DestinationConfigurationDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListDestinations(CancellationToken cancellationToken)
    {
        var destinations = await _repository.GetDestinationsAsync(cancellationToken);
        return Ok(destinations.Select(ConfigurationMapper.ToDto).ToArray());
    }

    [HttpGet("mapping-profiles")]
    [ProducesResponseType(typeof(IReadOnlyList<MappingProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListMappingProfiles(CancellationToken cancellationToken)
    {
        var mappings = await _repository.GetMappingProfilesAsync(cancellationToken);
        return Ok(mappings.Select(ConfigurationMapper.ToDto).ToArray());
    }
}
