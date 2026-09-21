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
    private readonly IFhirDestinationConnectionTestService _fhirConnectionTestService;
    private readonly IMedplumDestinationConnectionTestService _medplumConnectionTestService;
    private readonly IMongoDestinationConnectionTestService _mongoConnectionTestService;
    private readonly IBlobDestinationConnectionTestService _blobConnectionTestService;
    private readonly IFabricDestinationConnectionTestService _fabricConnectionTestService;

    public DestinationSchemaController(
        IDestinationSchemaService schemaService,
        ICsvDestinationConnectionTestService csvConnectionTestService,
        IFhirDestinationConnectionTestService fhirConnectionTestService,
        IMedplumDestinationConnectionTestService medplumConnectionTestService,
        IMongoDestinationConnectionTestService mongoConnectionTestService,
        IBlobDestinationConnectionTestService blobConnectionTestService,
        IFabricDestinationConnectionTestService fabricConnectionTestService)
    {
        _schemaService = schemaService;
        _csvConnectionTestService = csvConnectionTestService;
        _fhirConnectionTestService = fhirConnectionTestService;
        _medplumConnectionTestService = medplumConnectionTestService;
        _mongoConnectionTestService = mongoConnectionTestService;
        _blobConnectionTestService = blobConnectionTestService;
        _fabricConnectionTestService = fabricConnectionTestService;
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
    /// Executes a real CREATE TABLE (auto-increment Id primary key, plus any requested columns and an
    /// optional FK to a parent table) against an ad-hoc SQL Server / Azure SQL connection. Always returns
    /// 200 — failures (bad connection, table/parent-table already exists or missing, etc.) come back as
    /// <c>success:false</c> + <c>error</c>.
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
    /// Executes a real ALTER TABLE ... ALTER COLUMN (data type change) and/or an sp_rename (column
    /// rename) against an ad-hoc SQL Server / Azure SQL connection. Always returns 200 — failures (bad
    /// connection, invalid identifier/type, incompatible data, name collision, etc.) come back as
    /// <c>success:false</c> + <c>error</c> so the mapping canvas can surface them inline.
    /// </summary>
    [HttpPost("schema/alter-column")]
    [StandardPermission(
        PermissionGroupCode.Configuration,
        PermissionActionCode.Write,
        description: "Rename a destination table column and/or change its data type via a real ALTER TABLE.")]
    [ProducesResponseType(typeof(SchemaMutationResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> AlterColumn(
        [FromBody] AlterColumnRequest request,
        CancellationToken cancellationToken)
        => Ok(await _schemaService.AlterColumnAsync(request, cancellationToken));

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
    [ProducesResponseType(typeof(FhirConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestFhirConnection(
        [FromBody] FhirConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _fhirConnectionTestService.TestConnectionAsync(request, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc Medplum connection for a not-yet-saved <c>Medplum</c> destination — mints an OAuth2 token
    /// from the supplied credentials and does a real <c>GET {baseUrl}/metadata</c>. Always returns 200 —
    /// connection failures come back as <c>connected:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("medplum-test")]
    [ProducesResponseType(typeof(ConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestMedplumConnection(
        [FromBody] MedplumConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _medplumConnectionTestService.TestConnectionAsync(request, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc MongoDB connection for a not-yet-saved <c>Mongo</c> destination — opens a client on the
    /// supplied connection string, runs a <c>ping</c>, and (on success) returns the database's real collection
    /// names so the form can offer them as an autocomplete. Always returns 200 — connection failures come back as
    /// <c>connected:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("mongo-test")]
    [ProducesResponseType(typeof(MongoConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestMongoConnection(
        [FromBody] MongoConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _mongoConnectionTestService.TestConnectionAsync(request, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc Azure Blob Storage connection for a not-yet-saved <c>BlobStorage</c> destination — builds
    /// the container client for the supplied auth mode and does a real reachability round-trip. Always returns
    /// 200 — connection failures come back as <c>connected:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("blob-test")]
    [ProducesResponseType(typeof(ConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestBlobConnection(
        [FromBody] BlobConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _blobConnectionTestService.TestConnectionAsync(request, cancellationToken));

    /// <summary>
    /// Tests an ad-hoc Microsoft Fabric connection for a not-yet-saved <c>DataFabricAzure</c> destination.
    /// Probes OneLake and, for the Warehouse landing mode, the Warehouse TDS endpoint as well — they use
    /// different token audiences and fail independently, so the result reports them separately rather than as a
    /// single pass/fail. Always returns 200; failures come back as <c>connected:false</c> + <c>error</c>.
    /// </summary>
    [HttpPost("fabric-test")]
    [ProducesResponseType(typeof(FabricConnectionTestResultDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestFabricConnection(
        [FromBody] FabricConnectionTestRequest request,
        CancellationToken cancellationToken)
        => Ok(await _fabricConnectionTestService.TestConnectionAsync(request, cancellationToken));
}
