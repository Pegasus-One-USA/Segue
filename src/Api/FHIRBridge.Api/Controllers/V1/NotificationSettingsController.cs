using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Notifications;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Global outbound email (SMTP) configuration — the Settings hub's Email Settings tab. A single row, admin-editable
/// instead of the "Email" section this replaced in appsettings.json. Gated by the same Configuration permission
/// group as the other Settings hub tabs (Branding, EHR Endpoints, Destination Connections).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/notification-settings")]
public sealed class NotificationSettingsController : ControllerBase
{
    private readonly INotificationSettingsService _service;
    private readonly IEmailSender _emailSender;

    public NotificationSettingsController(INotificationSettingsService service, IEmailSender emailSender)
    {
        _service = service;
        _emailSender = emailSender;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.View, description: "View the outbound email configuration.")]
    [ProducesResponseType(typeof(NotificationSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var settings = await _service.GetAsync(cancellationToken);
        return Ok(settings);
    }

    [HttpPut]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Update the outbound email configuration.")]
    [ProducesResponseType(typeof(NotificationSettingsDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update(
        [FromBody] UpdateNotificationSettingsRequest request,
        CancellationToken cancellationToken)
    {
        var settings = await _service.UpdateAsync(request, cancellationToken);
        return Ok(settings);
    }

    /// <summary>
    /// Sends a test email using the currently saved settings — save first, then test. Reuses the same
    /// <see cref="IEmailSender"/> every other notification goes through, so a successful test is a real end-to-end
    /// check of Host/Port/EnableSsl/Username/Password/FromAddress, not a separate code path that could pass while
    /// the real one fails.
    /// </summary>
    [HttpPost("test-send")]
    [StandardPermission(PermissionGroupCode.Configuration, PermissionActionCode.Write, description: "Send a test email using the saved configuration.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> TestSend(
        [FromBody] SendTestEmailRequest request,
        CancellationToken cancellationToken)
    {
        await _emailSender.SendAsync(
            request.ToEmail,
            "Segue test email",
            "<p>This is a test email from Segue confirming your email settings are configured correctly.</p>",
            cancellationToken);

        return NoContent();
    }
}
