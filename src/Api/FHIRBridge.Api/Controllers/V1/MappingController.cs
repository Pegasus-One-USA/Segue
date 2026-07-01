using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/mapping")]
public sealed class MappingController : ControllerBase
{
    private readonly IJsonMappingEngine _mappingEngine;
    private readonly IFhirElementCatalog _catalog;

    public MappingController(IJsonMappingEngine mappingEngine, IFhirElementCatalog catalog)
    {
        _mappingEngine = mappingEngine;
        _catalog = catalog;
    }

    [HttpPost("test")]
    [ProducesResponseType(typeof(MappingTestResultDto), StatusCodes.Status200OK)]
    public IActionResult TestMapping([FromBody] TestMappingRequest request)
    {
        var result = _mappingEngine.Map(request.SourceJson, request.Fields);

        return Ok(result);
    }

    [HttpGet("catalog/resources")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public IActionResult GetCatalogResources()
    {
        return Ok(_catalog.ResourceTypes);
    }

    [HttpGet("catalog/resources/{resourceType}/fields")]
    [ProducesResponseType(typeof(IReadOnlyList<FhirElementDto>), StatusCodes.Status200OK)]
    public IActionResult GetCatalogFields(string resourceType)
    {
        var fields = _catalog.Fields(resourceType);
        if (fields.Count == 0)
        {
            return NotFound();
        }

        return Ok(fields);
    }
}
