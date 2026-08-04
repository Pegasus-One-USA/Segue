using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Abstractions.Terminology;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

[ApiController]
[Authorize]
[Route("api/v1/terminology/loinc/configuration")]
public sealed class LoincConfigurationController : ControllerBase
{
    private readonly ILoincConfigurationService _service;
    private readonly ILoincSynchronizationService _synchronization;
    public LoincConfigurationController(ILoincConfigurationService service, ILoincSynchronizationService synchronization) => (_service, _synchronization) = (service, synchronization);

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View LOINC terminology configuration.")]
    public async Task<ActionResult<LoincConfigurationDto>> Get(CancellationToken cancellationToken) => Ok(await _service.GetAsync(cancellationToken));

    [HttpPut]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Update LOINC terminology configuration and credentials.")]
    public async Task<ActionResult<LoincConfigurationDto>> Update([FromBody] UpdateLoincConfigurationRequest request, CancellationToken cancellationToken) => Ok(await _service.UpdateAsync(request, cancellationToken));

    [HttpPost("synchronize")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Manually synchronize the LOINC release.")]
    public async Task<IActionResult> Synchronize(CancellationToken cancellationToken) => Ok(await _synchronization.SynchronizeAsync(cancellationToken));
}
