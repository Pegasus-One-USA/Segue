using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/source-connections/{sourceConnectionId:guid}")]
public sealed class SourceCapabilitiesController : ControllerBase
{
    // Interaction codes that mean "this source can hand us this resource type" (read direction).
    private static readonly string[] ReadInteractions = ["read", "search-type", "search"];

    private readonly ISourceCapabilityDiscoveryService _discoveryService;
    private readonly IFhirElementCatalog _catalog;

    public SourceCapabilitiesController(
        ISourceCapabilityDiscoveryService discoveryService,
        IFhirElementCatalog catalog)
    {
        _discoveryService = discoveryService;
        _catalog = catalog;
    }

    /// <summary>Fetches the source's CapabilityStatement, persists a fresh snapshot, and returns it.</summary>
    [HttpPost("capabilities/discover")]
    [ProducesResponseType(typeof(SourceCapabilityProfileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Discover(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var capability = await _discoveryService.DiscoverAsync(sourceConnectionId, cancellationToken);

        return Ok(capability);
    }

    /// <summary>
    /// Fetches the source's public SMART discovery document (<c>.well-known/smart-configuration</c>) and returns its
    /// advertised OAuth endpoints + capabilities. Used to configure an interactive authorization-code connection.
    /// </summary>
    [HttpGet("smart-configuration")]
    [ProducesResponseType(typeof(SmartConfigurationDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DiscoverSmartConfiguration(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var configuration = await _discoveryService.DiscoverSmartConfigurationAsync(
            sourceConnectionId,
            cancellationToken);

        return Ok(configuration);
    }

    /// <summary>Returns the latest persisted capability snapshot, or 404 if discovery has never run.</summary>
    [HttpGet("capabilities")]
    [ProducesResponseType(typeof(SourceCapabilityProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var capability = await _discoveryService.GetAsync(sourceConnectionId, cancellationToken);

        return capability is null ? NotFound() : Ok(capability);
    }

    /// <summary>
    /// Returns every FHIR resource type from the element catalog, annotated with whether this source can provide
    /// it. Drives the resource-type dropdown gating in the mapping editor UI. When no capability snapshot exists
    /// yet, all resource types are reported as available with a "not yet discovered" reason.
    /// </summary>
    [HttpGet("catalog/resources")]
    [ProducesResponseType(typeof(IReadOnlyList<CatalogResourceAvailabilityDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalogResourceAvailability(
        Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var capability = await _discoveryService.GetAsync(sourceConnectionId, cancellationToken);

        var result = _catalog.ResourceTypes
            .Select(resourceType => Annotate(resourceType, capability))
            .ToList();

        return Ok(result);
    }

    private static CatalogResourceAvailabilityDto Annotate(string resourceType, SourceCapabilityProfileDto? capability)
    {
        if (capability is null)
        {
            return new CatalogResourceAvailabilityDto(
                resourceType,
                true,
                "Source capabilities have not been discovered yet.");
        }

        var supported = capability.Resources.Any(resource =>
            string.Equals(resource.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase) &&
            resource.Interactions.Any(interaction => ReadInteractions.Contains(interaction)));

        return new CatalogResourceAvailabilityDto(
            resourceType,
            supported,
            supported ? null : "Not supported by this source's capability statement.");
    }
}
