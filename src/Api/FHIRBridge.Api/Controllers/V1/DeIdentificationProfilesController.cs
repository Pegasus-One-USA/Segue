using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Minimal CRUD for <see cref="Application.DTOs.DeIdentificationProfileDto"/> — just enough to populate a
/// dropdown and create one inline (destination wizard, Transformation Rules screen), plus a preview endpoint
/// that evaluates a profile's rules against a sample resource with no destination/pipeline involved. There is
/// deliberately no separate management screen; rules for a profile are authored on the Transformation Rules
/// screen (pre-mapping, Global/ResourceType scope, tagged with a profile id).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/deidentification/profiles")]
public sealed class DeIdentificationProfilesController : ControllerBase
{
    private readonly IDeIdentificationProfileService _profileService;

    public DeIdentificationProfilesController(IDeIdentificationProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<DeIdentificationProfileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var profiles = await _profileService.ListAsync(cancellationToken);
        return Ok(profiles);
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(DeIdentificationProfileDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create(
        [FromBody] CreateDeIdentificationProfileRequest request, CancellationToken cancellationToken)
    {
        var profile = await _profileService.CreateAsync(request, cancellationToken);
        return Created($"/api/v1/deidentification/profiles/{profile.Id}", profile);
    }

    [HttpPost("{profileId:guid}/preview")]
    [ProducesResponseType(typeof(DeIdentificationPreviewResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview(
        Guid profileId, [FromBody] DeIdentificationPreviewRequest request, CancellationToken cancellationToken)
    {
        var result = await _profileService.PreviewAsync(profileId, request, cancellationToken);
        return Ok(result);
    }
}
