using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Cds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Admin CRUD over the EHRs (JWT issuers) allowed to call Segue's CDS Hooks services. Holds public keys only.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/cds/trusted-issuers")]
public sealed class CdsTrustedIssuersController : ControllerBase
{
    private readonly ICdsTrustedIssuerService _service;

    public CdsTrustedIssuersController(ICdsTrustedIssuerService service)
    {
        _service = service;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(IReadOnlyList<CdsTrustedIssuerDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) =>
        Ok(await _service.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsTrustedIssuerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var issuer = await _service.GetByIdAsync(id, cancellationToken);
        return issuer is null ? NotFound() : Ok(issuer);
    }

    [HttpPost]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Create, description: "Add a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsTrustedIssuerDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] SaveCdsTrustedIssuerRequest request, CancellationToken cancellationToken)
    {
        var issuer = await _service.CreateAsync(request, cancellationToken);
        return Created($"/api/v1/cds/trusted-issuers/{issuer.Id}", issuer);
    }

    [HttpPut("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Edit, description: "Edit a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsTrustedIssuerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCdsTrustedIssuerRequest request, CancellationToken cancellationToken) =>
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

    /// <summary>
    /// Checks that Segue can reach this issuer's FHIR server for callbacks: GET {fhirBase}/metadata with no token
    /// and no patient data. Resolved per call so the CRUD endpoints never depend on it.
    /// </summary>
    [HttpPost("{id:guid}/test-callback")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Test, description: "Run a test request against a CDS Hooks service.")]
    [ProducesResponseType(typeof(CdsCallbackTestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestCallback(
        Guid id,
        [FromServices] ICdsCallbackTestService callbackTestService,
        CancellationToken cancellationToken) =>
        Ok(await callbackTestService.TestAsync(id, cancellationToken));
}
