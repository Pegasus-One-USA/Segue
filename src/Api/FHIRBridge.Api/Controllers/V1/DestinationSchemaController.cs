using FHIRBridge.Api.Security;
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

    public DestinationSchemaController(
        IDestinationSchemaService schemaService,
        ICsvDestinationConnectionTestService csvConnectionTestService)
    {
        _schemaService = schemaService;
        _csvConnectionTestService = csvConnectionTestService;
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
    /// Executes a real ALTER TABLE against an ad-hoc SQL Server / Azure SQL connection. Always returns 200 —
    /// failures (bad connection, invalid identifier/type, column already exists, etc.) come back as
    /// <c>success:false</c> + <c>error</c> so the mapping canvas can surface them inline.
    /// </summary>
    [HttpPost("schema/add-column")]
    [StandardPermission(
        PermissionGroupCode.Configuration,
        PermissionActionCode.Write,
        description: "Add a column to a destination table via a real ALTER TABLE.")]
    [ProducesResponseType(typeof(SchemaMutationResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AddColumn(
        [FromBody] AddColumnRequest request,
        CancellationToken cancellationToken)
        => Ok(await _schemaService.AddColumnAsync(request, cancellationToken));

    /// <summary>
    /// Executes a real CREATE TABLE (single auto-increment Id primary key) against an ad-hoc SQL Server /
    /// Azure SQL connection. Always returns 200 — failures (bad connection, table already exists, etc.)
    /// come back as <c>success:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("schema/create-table")]
    [StandardPermission(
        PermissionGroupCode.Configuration,
        PermissionActionCode.Write,
        description: "Create a new destination table via a real CREATE TABLE.")]
    [ProducesResponseType(typeof(SchemaMutationResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> CreateTable(
        [FromBody] CreateTableRequest request,
        CancellationToken cancellationToken)
        => Ok(await _schemaService.CreateTableAsync(request, cancellationToken));

    /// <summary>
    /// Executes a real, irreversible ALTER TABLE ... DROP COLUMN against an ad-hoc SQL Server / Azure SQL
    /// connection — permanently deletes the column and any data in it. Always returns 200 — failures come
    /// back as <c>success:false</c> + <c>error</c>. Confirming this with the user is the caller's
    /// responsibility; this endpoint executes unconditionally once called.
    /// </summary>
    [HttpPost("schema/drop-column")]
    [StandardPermission(
        PermissionGroupCode.Configuration,
        PermissionActionCode.Write,
        description: "Permanently drop a column from a destination table via a real ALTER TABLE.")]
    [ProducesResponseType(typeof(SchemaMutationResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> DropColumn(
        [FromBody] DropColumnRequest request,
        CancellationToken cancellationToken)
        => Ok(await _schemaService.DropColumnAsync(request, cancellationToken));

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
}
