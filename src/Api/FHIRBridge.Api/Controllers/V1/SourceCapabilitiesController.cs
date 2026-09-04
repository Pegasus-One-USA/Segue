using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
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
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IScopeGeneratorService _scopeGenerator;

    public SourceCapabilitiesController(
        ISourceCapabilityDiscoveryService discoveryService,
        IFhirElementCatalog catalog,
        IConfigurationRepository configurationRepository,
        IScopeGeneratorService scopeGenerator)
    {
        _discoveryService = discoveryService;
        _catalog = catalog;
        _configurationRepository = configurationRepository;
        _scopeGenerator = scopeGenerator;
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
            overrideBaseUrl: null,
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

    /// <summary>
    /// Server-side scope generation for a source: derives the SMART scope set from the source's application type, the
    /// caller's selected <paramref name="resources"/>, and the scope version — then validates it against the source's
    /// live <c>scopes_supported</c> (best-effort; if the SMART discovery document is unreachable the scopes are still
    /// generated, just un-validated). This is the single source of truth so the wizard shows exactly what the pipeline
    /// will request. When <paramref name="scopeVersion"/> is omitted it is detected from the discovered capabilities.
    /// </summary>
    [HttpGet("derived-config")]
    [ProducesResponseType(typeof(GeneratedScopesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDerivedConfig(
        Guid sourceConnectionId,
        [FromQuery] string? resources,
        [FromQuery] string? scopeVersion,
        CancellationToken cancellationToken)
    {
        var source = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        var resourceList = (resources ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Best-effort discovery: the SMART document is public (no token). Failure must not break scope generation.
        IReadOnlyCollection<string>? supported = null;
        var version = string.IsNullOrWhiteSpace(scopeVersion) ? "v2" : scopeVersion;
        var detected = false;
        try
        {
            var smart = await _discoveryService.DiscoverSmartConfigurationAsync(sourceConnectionId, overrideBaseUrl: null, cancellationToken);
            supported = smart.ScopesSupported;
            if (string.IsNullOrWhiteSpace(scopeVersion))
            {
                var detectedVersion = DetectScopeVersion(smart.Capabilities, smart.ScopesSupported);
                if (detectedVersion is not null)
                {
                    version = detectedVersion;
                    detected = true;
                }
            }
        }
        catch
        {
            // Discovery unavailable (source not reachable / misconfigured) — generate without validation.
        }

        var result = _scopeGenerator.Generate(
            source.ApplicationType, resourceList, version, detected, supported, source.SourceSystemType);
        return Ok(result);
    }

    // Detects the SMART scope version: prefers permission-v1/permission-v2 capability tokens, else infers from the
    // shape of scopes_supported (granular v2 suffix like .rs/.cruds vs coarse v1 .read/.write). Null when inconclusive.
    private static string? DetectScopeVersion(
        IReadOnlyList<string> capabilities,
        IReadOnlyList<string> scopesSupported)
    {
        if (capabilities.Contains("permission-v2"))
        {
            return "v2";
        }

        if (capabilities.Contains("permission-v1"))
        {
            return "v1";
        }

        static string Suffix(string s) => (s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..] : string.Empty).ToLowerInvariant();
        if (scopesSupported.Any(s => Suffix(s).Length > 0 && Suffix(s).All(c => "cruds".Contains(c))))
        {
            return "v2";
        }

        if (scopesSupported.Any(s => Suffix(s) is "read" or "write"))
        {
            return "v1";
        }

        return null;
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
