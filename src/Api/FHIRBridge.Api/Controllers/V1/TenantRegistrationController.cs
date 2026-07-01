using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[AllowAnonymous]
[Route("api/v1/register")]
public sealed class TenantRegistrationController : ControllerBase
{
    private readonly ITenantRegistrationService _tenantRegistrationService;

    public TenantRegistrationController(ITenantRegistrationService tenantRegistrationService)
    {
        _tenantRegistrationService = tenantRegistrationService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(RegisterTenantResponse), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterTenantRequest request,
        CancellationToken cancellationToken)
    {
        var response = await _tenantRegistrationService.RegisterAsync(request, cancellationToken);

        return Created($"/api/v1/users/{response.UserId}", response);
    }
}
