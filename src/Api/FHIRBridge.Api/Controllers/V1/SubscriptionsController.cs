using FHIRBridge.Application.Abstractions.Pipeline;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Manages rest-hook FHIR <c>Subscription</c> resources on the configured source server (outbound FHIR
/// Subscriptions): register one so the source pushes changes to FHIRBridge's webhook endpoint, or delete one.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/subscriptions")]
public sealed class SubscriptionsController : ControllerBase
{
    private readonly IFhirSubscriptionManagementService _subscriptionManagement;

    public SubscriptionsController(IFhirSubscriptionManagementService subscriptionManagement)
    {
        _subscriptionManagement = subscriptionManagement;
    }

    [HttpPost]
    [ProducesResponseType(typeof(SubscriptionRegistrationResult), StatusCodes.Status201Created)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterSubscriptionApiRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _subscriptionManagement.RegisterAsync(
            new RegisterSubscriptionCommand(
                request.SourceConnectionId,
                request.Criteria,
                request.CallbackUrl,
                request.Headers,
                request.Reason),
            cancellationToken);

        return Created($"/api/v1/subscriptions/{result.Id}", result);
    }

    [HttpDelete("{subscriptionId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(
        string subscriptionId,
        [FromQuery] Guid sourceConnectionId,
        CancellationToken cancellationToken)
    {
        await _subscriptionManagement.DeleteAsync(sourceConnectionId, subscriptionId, cancellationToken);

        return NoContent();
    }
}

/// <summary>Request body for registering a Subscription on the source.</summary>
public sealed record RegisterSubscriptionApiRequest(
    Guid SourceConnectionId,
    string Criteria,
    string CallbackUrl,
    IReadOnlyCollection<string>? Headers = null,
    string? Reason = null);
