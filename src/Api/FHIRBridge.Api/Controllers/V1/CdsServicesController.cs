using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Cds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Admin CRUD over the CDS Hooks service instances Segue publishes at /cds-services/{serviceId}, plus the
/// Test screen's sample request and run. The public CDS Hooks endpoints themselves live in CdsHooksController.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/cds/services")]
public sealed class CdsServicesController : ControllerBase
{
    private readonly ICdsServiceInstanceService _service;

    public CdsServicesController(ICdsServiceInstanceService service)
    {
        _service = service;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(IReadOnlyList<CdsServiceInstanceDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await _service.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsServiceInstanceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var instance = await _service.GetByIdAsync(id, cancellationToken);
        return instance is null ? NotFound() : Ok(instance);
    }

    [HttpPost]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Create, description: "Add a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsServiceInstanceDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] SaveCdsServiceInstanceRequest request, CancellationToken cancellationToken)
    {
        var instance = await _service.CreateAsync(request, cancellationToken);
        return Created($"/api/v1/cds/services/{instance.Id}", instance);
    }

    [HttpPut("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Edit, description: "Edit a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsServiceInstanceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCdsServiceInstanceRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Delete, description: "Delete a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>The handler's synthetic CDS request for this instance's hook — the Test screen's starting point.</summary>
    [HttpGet("{id:guid}/sample-request")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsRequest), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSampleRequest(Guid id, CancellationToken cancellationToken) =>
        Ok(await _service.GetSampleRequestAsync(id, cancellationToken));

    /// <summary>
    /// The Epic setup sheet: every value the health system must configure by hand for this service (endpoint,
    /// audience, prefetch, override reason codes, fix codes, SMART links, issuers). Configuration only, no PHI.
    /// Resolved per call so the CRUD endpoints never depend on it.
    /// </summary>
    [HttpGet("{id:guid}/setup-sheet")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsSetupSheetDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSetupSheet(
        Guid id,
        [FromServices] ICdsSetupSheetService setupSheetService,
        CancellationToken cancellationToken) =>
        Ok(await setupSheetService.GetAsync(id, $"{Request.Scheme}://{Request.Host}{Request.PathBase}", cancellationToken));

    /// <summary>
    /// Runs a pasted CDS request against this instance without a JWT. Nothing is written to the CDS call log.
    /// The test service is resolved per call so the CRUD endpoints above never depend on it.
    /// </summary>
    [HttpPost("{id:guid}/test")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Test, description: "Run a test request against a CDS Hooks service.")]
    [ProducesResponseType(typeof(CdsTestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Test(
        Guid id,
        [FromBody] CdsRequest request,
        [FromServices] ICdsTestService testService,
        CancellationToken cancellationToken) =>
        Ok(await testService.RunAsync(id, request, cancellationToken));
}
