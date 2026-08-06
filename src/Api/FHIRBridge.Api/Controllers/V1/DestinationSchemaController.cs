using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Relational-destination schema access for the pipeline builder: introspect a saved destination, or
/// test an ad-hoc (unsaved) connection and load its tables/columns for the mapping UI's column pickers.
/// </summary>
[ApiController]
[Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
[Route("api/v1/destinations")]
public sealed class DestinationSchemaController : ControllerBase
{
    private readonly IDestinationSchemaService _schemaService;
    private readonly ICsvDestinationConnectionTestService _csvConnectionTestService;
    private readonly IFhirDestinationConnectionTestService _fhirConnectionTestService;

    public DestinationSchemaController(
        IDestinationSchemaService schemaService,
        ICsvDestinationConnectionTestService csvConnectionTestService,
        IFhirDestinationConnectionTestService fhirConnectionTestService)
    {
        _schemaService = schemaService;
        _csvConnectionTestService = csvConnectionTestService;
        _fhirConnectionTestService = fhirConnectionTestService;
    }

    /// <summary>Tables/columns of an already-saved relational destination.</summary>
    [HttpGet("{destinationId:guid}/schema")]
    [ProducesResponseType(typeof(DestinationSchemaDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetSchema(Guid destinationId, CancellationToken cancellationToken)
        => Ok(await _schemaService.GetSchemaAsync(destinationId, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc connection and, on success, returns its tables/columns. Always returns 200 — connection
    /// failures come back as <c>connected:false</c> + <c>error</c> so the builder can surface them inline.
    /// </summary>
    [HttpPost("schema-preview")]
    [ProducesResponseType(typeof(DestinationSchemaProbeDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> PreviewSchema(
        [FromBody] DestinationConnectionProbeRequest request,
        CancellationToken cancellationToken)
        => Ok(await _schemaService.ProbeSchemaAsync(request, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc SFTP connection for a CSV destination (storageType 'sftp'). Always returns 200 —
    /// connection failures come back as <c>connected:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("sftp-test")]
    [ProducesResponseType(typeof(ConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSftpConnection(
        [FromBody] SftpConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _csvConnectionTestService.TestSftpConnectionAsync(request, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc FHIR-repository connection (e.g. Aidbox) for a not-yet-saved <c>FhirRepository</c>
    /// destination. Always returns 200 — connection failures come back as <c>connected:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("fhir-test")]
    [ProducesResponseType(typeof(ConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestFhirConnection(
        [FromBody] FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _fhirConnectionTestService.TestConnectionAsync(request, cancellationToken));
}
