using System.Text;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Tabular;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Setup for the CSV / SQL Table source node: upload a CSV (stored encrypted), keep a SQL connection string as a
/// secret, list template presets and preview what the templates build. Gated by the TabularSources permission
/// group, because files and previews carry patient rows. Previewing needs Edit, not just View: it decrypts data.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/tabular-sources")]
public sealed class TabularSourcesController : ControllerBase
{
    private readonly ITabularSourceService _service;

    public TabularSourcesController(ITabularSourceService service)
    {
        _service = service;
    }

    [HttpGet("files")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.View, description: "List uploaded CSV files for CSV / SQL Table sources.")]
    [ProducesResponseType(typeof(IReadOnlyList<TabularSourceFileDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListFiles(CancellationToken cancellationToken) =>
        Ok(await _service.ListAsync(cancellationToken));

    [HttpGet("files/{id:guid}")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.View, description: "View an uploaded CSV file's description (never its content).")]
    [ProducesResponseType(typeof(TabularSourceFileDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetFile(Guid id, CancellationToken cancellationToken) =>
        Ok(await _service.GetAsync(id, cancellationToken));

    [HttpPost("files")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Create, description: "Upload a CSV file for a CSV / SQL Table source.")]
    [RequestSizeLimit(TabularSourceSettings.MaxFileBytes + (64 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = TabularSourceSettings.MaxFileBytes + (64 * 1024))]
    [ProducesResponseType(typeof(TabularSourceFileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UploadFile(IFormFile file, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return BadRequest(new { error = "invalid_request", error_description = "Choose a CSV file to upload." });
        }

        if (file.Length > TabularSourceSettings.MaxFileBytes)
        {
            return BadRequest(new { error = "invalid_request", error_description = "The file is larger than 10 MB." });
        }

        using var reader = new StreamReader(file.OpenReadStream(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return Ok(await _service.UploadAsync(file.FileName, content, cancellationToken));
    }

    [HttpDelete("files/{id:guid}")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Delete, description: "Delete an uploaded CSV file and its data.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteFile(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("sql-connections")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Edit, description: "Store a database connection string for a CSV / SQL Table source as a secret.")]
    [ProducesResponseType(typeof(TabularSqlConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> SaveSqlConnection(
        [FromBody] SaveTabularSqlConnectionRequest request,
        CancellationToken cancellationToken) =>
        Ok(await _service.SaveSqlConnectionAsync(request, cancellationToken));

    [HttpGet("sql-connections")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.View, description: "List saved databases for CSV / SQL Table sources (names only, never connection strings).")]
    [ProducesResponseType(typeof(IReadOnlyList<TabularSqlConnectionDto>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSqlConnections(CancellationToken cancellationToken) =>
        Ok(await _service.ListSqlConnectionsAsync(cancellationToken));

    [HttpPut("sql-connections/{id:guid}")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Edit, description: "Rename a saved database or replace its connection string.")]
    [ProducesResponseType(typeof(TabularSqlConnectionDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> UpdateSqlConnection(
        Guid id, [FromBody] UpdateTabularSqlConnectionRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.UpdateSqlConnectionAsync(id, request, cancellationToken));

    [HttpDelete("sql-connections/{id:guid}")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Delete, description: "Delete a saved database from the list.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> DeleteSqlConnection(Guid id, CancellationToken cancellationToken)
    {
        await _service.DeleteSqlConnectionAsync(id, cancellationToken);
        return NoContent();
    }

    [HttpPost("sql-connections/{id:guid}/test")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Edit, description: "Test a saved database: connect, check the login can only read, run SELECT 1.")]
    [ProducesResponseType(typeof(TabularSqlConnectionTestDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> TestSqlConnection(Guid id, CancellationToken cancellationToken) =>
        Ok(await _service.TestSqlConnectionAsync(id, cancellationToken));

    [HttpGet("template-presets")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.View, description: "List the starting row-to-FHIR templates.")]
    [ProducesResponseType(typeof(IReadOnlyList<TabularTemplatePresetDto>), StatusCodes.Status200OK)]
    public IActionResult TemplatePresets() =>
        Ok(TabularTemplatePresets.All.Select(p => new TabularTemplatePresetDto(p.Key, p.Value)).ToList());

    [HttpPost("preview")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Edit, description: "Preview the FHIR resources a CSV / SQL Table source builds from its first rows.")]
    [ProducesResponseType(typeof(TabularPreviewDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Preview([FromBody] TabularPreviewRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.PreviewAsync(request, cancellationToken));

    // Edit, like preview: it uses the saved connection, and with Preview set it reads rows.
    [HttpPost("check")]
    [StandardPermission(PermissionGroupCode.TabularSources, PermissionActionCode.Edit, description: "Check each resource type's query or file against its connection, and optionally preview its first rows.")]
    [ProducesResponseType(typeof(TabularCheckDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> Check([FromBody] TabularCheckRequest request, CancellationToken cancellationToken) =>
        Ok(await _service.CheckAsync(request, cancellationToken));
}
