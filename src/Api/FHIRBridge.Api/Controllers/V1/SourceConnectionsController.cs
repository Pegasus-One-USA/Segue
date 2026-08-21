using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Read/delete endpoints for the <c>SourceConnection</c> configuration entity. Listing already lives on
/// <see cref="ConfigurationCatalogController"/> (<c>GET /api/v1/source-connections</c>) and create/update stay on
/// <see cref="ConfigurationsController"/> (their permission depends on the connection's vendor, resolved at
/// runtime) — this controller only adds the two operations neither of those expose yet: fetch a single source
/// connection by id, and delete one. Gated by the flat (vendor-independent) SourceConnections/View and
/// SourceConnections/Delete permissions the Source Connections admin page checks on the frontend, AND —
/// additionally, independently — by the connection's own vendor-specific View/Delete permission, resolved
/// from the fetched row's SourceSystemType (the id alone doesn't reveal the vendor).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/source-connections")]
public sealed class SourceConnectionsController : ControllerBase
{
    private readonly IConfigurationService _configurationService;
    private readonly ISigningKeyGenerationService _signingKeyGenerationService;
    private readonly IAuthorizationService _authorizationService;

    public SourceConnectionsController(
        IConfigurationService configurationService,
        ISigningKeyGenerationService signingKeyGenerationService,
        IAuthorizationService authorizationService)
    {
        _configurationService = configurationService;
        _signingKeyGenerationService = signingKeyGenerationService;
        _authorizationService = authorizationService;
    }

    /// <summary>
    /// Generates a new SMART Backend Services (<c>private_key_jwt</c>) RSA key pair and stores the private key in
    /// the secret store. Not scoped to an existing source connection — the wizard calls this before a connection is
    /// saved, then carries the returned key reference into the connection's Authentication config on create/update.
    /// The private key itself is never returned; only the (Key Vault name, secret name, key id) needed to wire it up.
    /// </summary>
    [HttpPost("generate-signing-key")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Edit, description: "Generate a SMART Backend Services signing key for a source connection.")]
    [ProducesResponseType(typeof(GeneratedSigningKeyDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GenerateSigningKey(CancellationToken cancellationToken)
    {
        var key = await _signingKeyGenerationService.GenerateAsync(cancellationToken);
        return Ok(key);
    }

    /// <summary>
    /// Validates and stores a customer-supplied SMART Backend Services (<c>private_key_jwt</c>) RSA private key —
    /// for a customer who already has an Epic app registered against their own key pair and wants FHIRBridge to
    /// sign with that same key instead of generating a new one. Rejects (400) anything that isn't a real,
    /// unencrypted RSA private key of at least 2048 bits — a public key, a certificate, a non-RSA key, or an
    /// encrypted PEM. Not scoped to an existing source connection, same as generate-signing-key.
    /// </summary>
    [HttpPost("import-signing-key")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Edit, description: "Import a SMART Backend Services signing key for a source connection.")]
    [ProducesResponseType(typeof(GeneratedSigningKeyDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportSigningKey(
        [FromBody] ImportSigningKeyRequest request,
        CancellationToken cancellationToken)
    {
        var key = await _signingKeyGenerationService.ImportAsync(request.PrivateKeyPem, cancellationToken);
        return Ok(key);
    }

    [HttpGet("{sourceConnectionId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.View, description: "View a source connection's configuration.")]
    [ProducesResponseType(typeof(SourceConnectionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.GetSourceConnectionByIdAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return NotFound();
        }

        // Two independent layers, same pattern as everywhere else: the generic SourceConnections.View
        // above AND this connection's own vendor's View permission.
        var denied = await this.AuthorizePermissionAsync(_authorizationService, sourceConnection.SourceSystemType, PermissionActionCode.View);
        if (denied is not null) return denied;

        return Ok(sourceConnection);
    }

    [HttpDelete("{sourceConnectionId:guid}")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Delete, description: "Delete a source connection.")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Delete, description: "Delete a source connection.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.GetSourceConnectionByIdAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return NotFound();
        }

        var denied = await this.AuthorizePermissionAsync(_authorizationService, sourceConnection.SourceSystemType, PermissionActionCode.Delete);
        if (denied is not null) return denied;

        await _configurationService.DeleteSourceConnectionAsync(sourceConnectionId, cancellationToken);
        return NoContent();
    }
}
