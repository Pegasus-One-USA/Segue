using System.Text;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs.Cds;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Cds;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Coverage requirements (Da Vinci CRD) payers: who Segue answers for, and whether a payer's calls are relayed to its
/// own CRD service. Configuration only — no patient data.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/cds/payers")]
public sealed class CdsPayersController : ControllerBase
{
    private readonly ICdsPayerService _service;

    public CdsPayersController(ICdsPayerService service)
    {
        _service = service;
    }

    [HttpGet]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(IReadOnlyList<CdsPayerDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken cancellationToken) => Ok(await _service.GetAllAsync(cancellationToken));

    [HttpGet("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsPayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken) =>
        await _service.GetByIdAsync(id, cancellationToken) is { } payer ? Ok(payer) : NotFound();

    [HttpPost]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Create, description: "Add a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsPayerDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] SaveCdsPayerRequest request, CancellationToken cancellationToken)
    {
        var payer = await _service.CreateAsync(request, cancellationToken);
        return Created($"/api/v1/cds/payers/{payer.Id}", payer);
    }

    [HttpPut("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Edit, description: "Edit a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsPayerDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCdsPayerRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.UpdateAsync(id, request, cancellationToken));

    /// <summary>Deletes the payer and all of its rules.</summary>
    [HttpDelete("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Delete, description: "Delete a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>Sends a synthetic, non-PHI order-sign request to the payer's CRD service, signed as Segue.</summary>
    [HttpPost("{id:guid}/test-relay")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Test, description: "Run a test request against a CDS Hooks service.")]
    [ProducesResponseType(typeof(CdsPayerRelayTestResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> TestRelay(Guid id, [FromServices] ICdsPayerRelayTestService testService, CancellationToken cancellationToken) =>
        Ok(await testService.TestAsync(id, cancellationToken));
}

/// <summary>
/// Coverage requirements rules: CRUD, paged list, file import (CSV / JSON, preview or apply) and export.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/cds/coverage-rules")]
public sealed class CdsCoverageRulesController : ControllerBase
{
    /// <summary>Largest import file (CSV or JSON).</summary>
    private const long MaxImportBytes = 5 * 1024 * 1024;

    private readonly ICdsCoverageRuleService _service;

    public CdsCoverageRulesController(ICdsCoverageRuleService service)
    {
        _service = service;
    }

    /// <param name="payerId">Only this payer's rules.</param>
    /// <param name="code">Code starts with this (case-insensitive).</param>
    /// <param name="search">Text in the system, code or reason.</param>
    /// <param name="enabled">true / false to filter on the switch.</param>
    [HttpGet]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsCoverageRulePageDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? payerId,
        [FromQuery] string? code,
        [FromQuery] string? search,
        [FromQuery] bool? enabled,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken cancellationToken) =>
        Ok(await _service.GetPagedAsync(
            new CdsCoverageRuleFilter(payerId, code, search, enabled), page <= 0 ? 1 : page, pageSize <= 0 ? 50 : pageSize, cancellationToken));

    [HttpGet("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(CdsCoverageRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken) =>
        await _service.GetByIdAsync(id, cancellationToken) is { } rule ? Ok(rule) : NotFound();

    [HttpPost]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Create, description: "Add a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsCoverageRuleDto), StatusCodes.Status201Created)]
    public async Task<IActionResult> Create([FromBody] SaveCdsCoverageRuleRequest request, CancellationToken cancellationToken)
    {
        var rule = await _service.CreateAsync(request, cancellationToken);
        return Created($"/api/v1/cds/coverage-rules/{rule.Id}", rule);
    }

    [HttpPut("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Edit, description: "Edit a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsCoverageRuleDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveCdsCoverageRuleRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.UpdateAsync(id, request, cancellationToken));

    [HttpDelete("{id:guid}")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Delete, description: "Delete a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Checks (mode=preview, writes nothing) or applies (mode=apply, all rows or none) a rule file. The file is the
    /// multipart part "file", or the raw request body (text/csv or application/json).
    /// </summary>
    /// <param name="format">csv | json (default: from the file name, else csv).</param>
    /// <param name="mode">preview (default) | apply.</param>
    /// <param name="strategy">merge (default) | replace (rules of the payers in the file that the file does not list are deleted).</param>
    /// <param name="payerId">Every row belongs to this payer (the payer column may then be left blank).</param>
    /// <remarks>Applying a replace import deletes rules, so it also needs the Delete permission (403 without it).</remarks>
    [HttpPost("import")]
    [RequestSizeLimit(MaxImportBytes + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxImportBytes + 64 * 1024)]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.Edit, description: "Edit a CDS Hooks service or trusted issuer.")]
    [ProducesResponseType(typeof(CdsCoverageRuleImportResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Import(
        [FromQuery] string? format,
        [FromQuery] string? mode,
        [FromQuery] string? strategy,
        [FromQuery] Guid? payerId,
        [FromServices] IAuthorizationService authorizationService,
        CancellationToken cancellationToken)
    {
        if (string.Equals(strategy?.Trim(), CdsCoverageFileFormats.StrategyReplace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(mode?.Trim(), CdsCoverageFileFormats.ModeApply, StringComparison.OrdinalIgnoreCase))
        {
            var denied = await this.AuthorizePermissionAsync(authorizationService, PermissionGroupCode.CdsHooks, PermissionActionCode.Delete);
            if (denied is not null)
            {
                return denied;
            }
        }

        string content;
        string? fileName = null;
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync(cancellationToken);
            var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return BadRequest(new { error = "Attach the rule file as the form field 'file'." });
            }

            if (file.Length > MaxImportBytes)
            {
                return BadRequest(new { error = "The file is larger than 5 MB." });
            }

            fileName = file.FileName;
            using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            content = await reader.ReadToEndAsync(cancellationToken);
        }
        else
        {
            using var reader = new StreamReader(Request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            content = await reader.ReadToEndAsync(cancellationToken);
        }

        var resolvedFormat = format
                             ?? (fileName?.EndsWith(".json", StringComparison.OrdinalIgnoreCase) == true
                                 || Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
                                 ? CdsCoverageFileFormats.Json
                                 : CdsCoverageFileFormats.Csv);
        return Ok(await _service.ImportAsync(
            resolvedFormat, mode ?? CdsCoverageFileFormats.ModePreview, strategy ?? CdsCoverageFileFormats.StrategyMerge, payerId, content, cancellationToken));
    }

    /// <summary>Downloads the rules (all payers, or one) as csv or json, in the import format.</summary>
    [HttpGet("export")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public async Task<IActionResult> Export([FromQuery] string? format, [FromQuery] Guid? payerId, CancellationToken cancellationToken)
    {
        var file = await _service.ExportAsync(format ?? CdsCoverageFileFormats.Csv, payerId, cancellationToken);
        return File(Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
    }

    /// <summary>An import template (header plus two made-up example rows).</summary>
    [HttpGet("import-template")]
    [StandardPermission(PermissionGroupCode.CdsHooks, PermissionActionCode.View, description: "View CDS Hooks services, trusted issuers and activity.")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    public IActionResult Template([FromQuery] string? format)
    {
        var file = _service.GetTemplate(format ?? CdsCoverageFileFormats.Csv);
        return File(Encoding.UTF8.GetBytes(file.Content), file.ContentType, file.FileName);
    }
}
