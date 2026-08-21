using FHIRBridge.Application.Abstractions.Destinations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Serves files produced by <see cref="Domain.Enums.ArtifactDeliveryMode.DownloadUrl"/> delivery. The token itself
/// is the authentication (HMAC-signed, expiring) — the caller is a third-party app or a person following a link, not
/// a portal user, so this is <see cref="AllowAnonymousAttribute"/> exactly like <c>WebhookIngestionController</c>.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/v1/generated-files")]
public sealed class GeneratedFileDownloadsController : ControllerBase
{
    private readonly IGeneratedFileDownloadLinkService _downloadLinkService;

    public GeneratedFileDownloadsController(IGeneratedFileDownloadLinkService downloadLinkService)
    {
        _downloadLinkService = downloadLinkService;
    }

    [HttpGet("{token}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string token, CancellationToken cancellationToken)
    {
        var resolution = await _downloadLinkService.TryResolveAsync(token, cancellationToken);
        if (resolution is null)
        {
            return NotFound();
        }

        var bytes = await _downloadLinkService.ReadDecryptedAsync(resolution.PhysicalPath, cancellationToken);
        return File(bytes, resolution.ContentType, resolution.DisplayFileName);
    }
}
