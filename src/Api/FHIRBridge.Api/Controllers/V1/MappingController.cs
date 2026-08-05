using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/mapping")]
public sealed class MappingController : ControllerBase
{
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IFhirElementCatalog _genericCatalog;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IServiceProvider _serviceProvider;

    public MappingController(
        IJsonMappingEngine mappingEngine,
        [FromKeyedServices(FhirElementCatalogKeys.Generic)] IFhirElementCatalog genericCatalog,
        IConfigurationRepository configurationRepository,
        IServiceProvider serviceProvider)
    {
        _mappingEngine = mappingEngine;
        _genericCatalog = genericCatalog;
        _configurationRepository = configurationRepository;
        _serviceProvider = serviceProvider;
    }

    [HttpPost("test")]
    [ProducesResponseType(typeof(MappingTestResultDto), StatusCodes.Status200OK)]
    public IActionResult TestMapping([FromBody] TestMappingRequest request)
    {
        var result = _mappingEngine.Map(request.SourceJson, request.Fields);

        return Ok(result);
    }

    // Deliberately still backed by the generic catalog only, even for Epic sources: this is the
    // resource-TYPE picker (Patient, Condition, ...), and only Patient has an Epic-specific catalog so
    // far (see fhir-r4-catalog.epic.json). Making this vendor-aware today would narrow an Epic source's
    // resource picker down to just "Patient" until every resource gets its own Epic template — a
    // regression versus what it shows now. Field-level lookup (below) is the one that's vendor-aware,
    // with a fallback to generic per resource type, so this can follow once more resources are converted.
    [HttpGet("catalog/resources")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public IActionResult GetCatalogResources()
    {
        return Ok(_genericCatalog.ResourceTypes);
    }

    /// <summary>
    /// Returns the field catalog for a resource type. When <paramref name="sourceConnectionId"/> is
    /// supplied, resolves that source's vendor and prefers its vendor-specific catalog (Epic today);
    /// falls back to the generic base-FHIR-R4 catalog for any resource type the vendor catalog doesn't
    /// cover yet, or when no source connection is given at all (unchanged from before this endpoint
    /// took a vendor into account).
    /// </summary>
    [HttpGet("catalog/resources/{resourceType}/fields")]
    [ProducesResponseType(typeof(IReadOnlyList<FhirElementDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetCatalogFields(
        string resourceType,
        [FromQuery] Guid? sourceConnectionId,
        CancellationToken cancellationToken)
    {
        var catalog = await ResolveCatalogAsync(sourceConnectionId, cancellationToken);
        var fields = catalog.Fields(resourceType);

        if (fields.Count == 0 && !ReferenceEquals(catalog, _genericCatalog))
        {
            fields = _genericCatalog.Fields(resourceType);
        }

        if (fields.Count == 0)
        {
            return NotFound();
        }

        return Ok(fields);
    }

    private async Task<IFhirElementCatalog> ResolveCatalogAsync(Guid? sourceConnectionId, CancellationToken cancellationToken)
    {
        if (sourceConnectionId is null)
        {
            return _genericCatalog;
        }

        var source = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId.Value, cancellationToken);
        if (source is null)
        {
            return _genericCatalog;
        }

        var key = FhirElementCatalogKeys.For(source.SourceSystemType);
        return _serviceProvider.GetRequiredKeyedService<IFhirElementCatalog>(key);
    }
}
