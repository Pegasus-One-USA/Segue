using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Workflows.Validation;

namespace FHIRBridge.Infrastructure.Workflows.Validation;

/// <summary>
/// A supplied hospital/organization must actually exist in the public endpoint directory.
/// <para>Deliberately does NOT require one. A launch with no endpoint override is legitimate and common — it runs
/// against the source connection's own base URL, which is how the vendors with a single fixed FHIR endpoint
/// (eCW, athenahealth) work, and how <c>OAuthController</c> already treats an absent id. Requiring one here would
/// break those flows outright. What was previously unchecked is a supplied id that resolves to nothing: the mint
/// call then returns a bare 404 the caller has to interpret, after the user has already picked a hospital.</para>
/// </summary>
public sealed class LaunchEndpointKnownRule : IWorkflowRunParameterRule
{
    // Every directory type a caller can legitimately launch against, across both Standalone audiences: Epic and
    // eCW are the Provider Standalone sandbox rows, MyChart the Patient Standalone ones. Checked as one set rather
    // than per-audience because this rule cannot tell which audience is calling — and a cross-audience id is
    // already rejected by the mint endpoint's own, narrower check.
    private static readonly EhrEndpointType[] LaunchableEndpointTypes =
        [EhrEndpointType.Epic, EhrEndpointType.Ecw, EhrEndpointType.MyChart];

    private readonly IEhrEndpointService _ehrEndpointService;

    public LaunchEndpointKnownRule(IEhrEndpointService ehrEndpointService)
    {
        _ehrEndpointService = ehrEndpointService;
    }

    public async Task<IReadOnlyList<WorkflowRunValidationError>> ValidateAsync(
        WorkflowRunValidationContext context,
        CancellationToken cancellationToken)
    {
        if (context.Parameters.EhrEndpointId is not { } endpointId)
        {
            return [];
        }

        var known = await _ehrEndpointService.IsKnownEndpointAsync(
            endpointId, LaunchableEndpointTypes, cancellationToken);

        return known
            ? []
            : [new WorkflowRunValidationError(
                "ehrEndpointId",
                "The selected hospital is no longer available. Pick one from the list and try again.")];
    }
}
