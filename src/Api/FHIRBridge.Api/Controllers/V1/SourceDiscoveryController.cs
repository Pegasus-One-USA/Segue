using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Pre-create endpoint discovery for the source-connection wizard: given only a FHIR base URL, fetch the SMART
/// discovery document (OAuth endpoints + scopes) and the supported resource types, so the wizard can auto-populate
/// those fields before any source connection exists. Post-create discovery stays on <see cref="SourceCapabilitiesController"/>.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/source-discovery")]
public sealed class SourceDiscoveryController : ControllerBase
{
    private readonly ISourceEndpointProbeService _probeService;
    private readonly IBackendAuthScopeProbeService _backendAuthScopeProbeService;

    public SourceDiscoveryController(
        ISourceEndpointProbeService probeService,
        IBackendAuthScopeProbeService backendAuthScopeProbeService)
    {
        _probeService = probeService;
        _backendAuthScopeProbeService = backendAuthScopeProbeService;
    }

    [HttpPost("probe")]
    [ProducesResponseType(typeof(SourceDiscoveryProbeResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Probe(
        [FromBody] SourceDiscoveryProbeRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.BaseUrl))
        {
            return BadRequest("baseUrl is required.");
        }

        // Both probes are best-effort: a plain (non-SMART) FHIR R4 server — e.g. a bare HAPI test instance — has no
        // /.well-known/smart-configuration at all, and some servers gate /metadata behind auth. Either failing
        // shouldn't fail the whole probe; the wizard falls back to manual endpoint entry / the static resource list.
        var smart = SmartConfigurationDto.Empty;
        string? smartConfigurationError = null;
        try
        {
            smart = await _probeService.ProbeSmartConfigurationAsync(request.BaseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            // Probing an arbitrary external URL — never echo the raw exception/upstream body back to the client.
            smartConfigurationError = FHIRBridge.Governance.SafeErrorText.SanitizeOr(
                ex.Message, "Could not read the endpoint's SMART configuration.");
        }

        IReadOnlyList<string> resourceTypes = [];
        string? resourceTypesError = null;
        try
        {
            resourceTypes = await _probeService.ProbeSupportedResourceTypesAsync(request.BaseUrl, cancellationToken);
        }
        catch (Exception ex)
        {
            resourceTypesError = FHIRBridge.Governance.SafeErrorText.SanitizeOr(
                ex.Message, "Could not read the endpoint's supported resource types.");
        }

        return Ok(new SourceDiscoveryProbeResult(smart, resourceTypes, resourceTypesError, smartConfigurationError));
    }

    /// <summary>
    /// Backend System (SMART Backend Services) only: performs a real client_credentials + private_key_jwt exchange
    /// against Epic's token endpoint using a signing key already provisioned into the secret store, and returns the
    /// scopes Epic actually granted the app — the wizard's Discover action calls this to show what the app is really
    /// allowed to do, as opposed to the scopes the server merely advertises support for.
    /// </summary>
    [HttpPost("backend-auth-scopes")]
    [ProducesResponseType(typeof(BackendAuthScopesResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BackendAuthScopes(
        [FromBody] BackendAuthScopesRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request?.TokenEndpoint) ||
            string.IsNullOrWhiteSpace(request.ClientId) ||
            string.IsNullOrWhiteSpace(request.PrivateKeyVaultName) ||
            string.IsNullOrWhiteSpace(request.PrivateKeySecretName))
        {
            return BadRequest("tokenEndpoint, clientId, privateKeyVaultName, and privateKeySecretName are required.");
        }

        var result = await _backendAuthScopeProbeService.ProbeGrantedScopesAsync(request, cancellationToken);
        return Ok(result);
    }
}
