using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Governance;
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
    private readonly ISourceSigningKeyExportService _signingKeyExportService;
    private readonly IGovernanceLogger _governanceLogger;
    private readonly ICurrentUserService _currentUserService;
    private readonly IAuthorizationService _authorizationService;

    public SourceConnectionsController(
        IConfigurationService configurationService,
        ISigningKeyGenerationService signingKeyGenerationService,
        ISourceSigningKeyExportService signingKeyExportService,
        IGovernanceLogger governanceLogger,
        ICurrentUserService currentUserService,
        IAuthorizationService authorizationService)
    {
        _configurationService = configurationService;
        _signingKeyGenerationService = signingKeyGenerationService;
        _signingKeyExportService = signingKeyExportService;
        _governanceLogger = governanceLogger;
        _currentUserService = currentUserService;
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GenerateSigningKey(
        [FromQuery] SourceSystemType? sourceSystemType, CancellationToken cancellationToken)
    {
        // Nullable, not a plain SourceSystemType: SourceSystemType.Sample is 0, so an omitted query parameter
        // (an older cached portal bundle, a direct API call) would otherwise silently bind to Sample instead of
        // failing — storing the generated key under a misleading "sample-private-key-..." name for whatever
        // vendor the caller actually meant, exactly the mislabeling this naming scheme exists to prevent.
        if (sourceSystemType is null)
        {
            return BadRequest("sourceSystemType is required.");
        }

        var key = await _signingKeyGenerationService.GenerateAsync(sourceSystemType.Value, cancellationToken);
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
        // See GenerateSigningKey's identical check: SourceSystemType.Sample is 0, so a missing/omitted JSON
        // property would otherwise silently deserialize to Sample instead of failing.
        if (request.SourceSystemType is null)
        {
            return BadRequest("sourceSystemType is required.");
        }

        var key = await _signingKeyGenerationService.ImportAsync(request.PrivateKeyPem, request.SourceSystemType.Value, cancellationToken);
        return Ok(key);
    }

    /// <summary>
    /// Downloads the public half of this connection's SMART Backend Services signing key as a PEM file — what an
    /// EHR app registration takes when it accepts an uploaded public key instead of a JWKS URL. Publishes nothing
    /// the anonymous <c>.well-known/jwks.json</c> endpoint doesn't already, so it's gated by the ordinary View
    /// permissions. 404 when the connection doesn't exist or has no asymmetric signing key configured.
    /// </summary>
    [HttpGet("{sourceConnectionId:guid}/signing-key/public.pem")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.View, description: "View the list of source connections.")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.View, description: "View a source connection's configuration.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadPublicKeyPem(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.GetSourceConnectionByIdAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return NotFound();
        }

        var denied = await this.AuthorizePermissionAsync(_authorizationService, sourceConnection.SourceSystemType, PermissionActionCode.View);
        if (denied is not null) return denied;

        var pem = await _signingKeyExportService.GetPublicKeyPemAsync(sourceConnectionId, cancellationToken);
        if (pem is null)
        {
            return NotFound();
        }

        return PemFile(pem, $"{FileNameStem(sourceConnection.Name)}-public.pem");
    }

    /// <summary>
    /// Downloads the PRIVATE key of this connection's SMART Backend Services signing key as a PEM file — for a
    /// customer keeping their own copy of a key FHIRBridge generated on their behalf. This is the one endpoint that
    /// hands real secret material back out of the secret store, so it carries its own dedicated
    /// <c>sourceconnections.export</c> permission (deliberately NOT implied by Edit, which provisions keys) on top
    /// of the connection's vendor-specific Edit permission, and every download is written to the security event log
    /// with the exporting user. 404 when the connection doesn't exist or has no asymmetric signing key configured.
    /// </summary>
    [HttpGet("{sourceConnectionId:guid}/signing-key/private.pem")]
    [StandardPermission(PermissionGroupCode.SourceConnections, PermissionActionCode.Export, description: "Download the private key PEM of a source connection's SMART Backend Services signing key.")]
    [DynamicSourceSystemPermission(typeof(SourceSystemType), PermissionActionCode.Edit, description: "Add or edit a source connection for this vendor.")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadPrivateKeyPem(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationService.GetSourceConnectionByIdAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return NotFound();
        }

        var denied = await this.AuthorizePermissionAsync(_authorizationService, sourceConnection.SourceSystemType, PermissionActionCode.Edit);
        if (denied is not null) return denied;

        var pem = await _signingKeyExportService.GetPrivateKeyPemAsync(sourceConnectionId, cancellationToken);
        if (pem is null)
        {
            return NotFound();
        }

        await _governanceLogger.LogSecurityEventAsync(
            new SecurityEventEntry(
                EventType: "SigningKeyPrivatePemExported",
                Severity: "Warning",
                UserEmail: _currentUserService.CurrentUser.Email,
                Details: $"Private signing key PEM downloaded for source connection '{sourceConnection.Name}' ({sourceConnectionId})."),
            cancellationToken);

        return PemFile(pem, $"{FileNameStem(sourceConnection.Name)}-private.pem");
    }

    /// <summary>
    /// PEM is text, but it's served as a download (never rendered inline) — <c>application/x-pem-file</c> plus an
    /// explicit file name is what makes the browser save it as the .pem an EHR app registration expects.
    /// </summary>
    private FileContentResult PemFile(string pem, string fileName)
    {
        return File(System.Text.Encoding.ASCII.GetBytes(pem), "application/x-pem-file", fileName);
    }

    /// <summary>Connection name reduced to a safe file-name stem — anything that isn't alphanumeric/dash becomes a
    /// dash, so a name with slashes, quotes or non-ASCII can't shape the Content-Disposition header.</summary>
    private static string FileNameStem(string? connectionName)
    {
        var stem = new string((connectionName ?? string.Empty)
            .Select(c => char.IsAsciiLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');

        return string.IsNullOrEmpty(stem) ? "source-connection" : stem;
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
