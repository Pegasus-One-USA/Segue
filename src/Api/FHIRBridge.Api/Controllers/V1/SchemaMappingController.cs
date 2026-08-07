using System.Text.Json;
using FHIRBridge.Api.Security;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.DTOs.Mapping;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services.Mapping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FHIRBridge.Api.Controllers.V1;

/// <summary>
/// Destination-driven schema matching: suggest, approve, and look up field-level mappings between a source
/// EHR JSON shape and a destination table/resource's columns. Suggestions are read-only (no persistence);
/// approving a mapping persists it and makes it override the scoring algorithm on every future request for
/// the same (source system, resource type, destination table, destination field).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/schema-mappings")]
public sealed class SchemaMappingController : ControllerBase
{
    private readonly ISchemaMatchingService _schemaMatchingService;
    private readonly IDestinationSchemaService _destinationSchemaService;

    public SchemaMappingController(
        ISchemaMatchingService schemaMatchingService,
        IDestinationSchemaService destinationSchemaService)
    {
        _schemaMatchingService = schemaMatchingService;
        _destinationSchemaService = destinationSchemaService;
    }

    /// <summary>
    /// Scores every column of <paramref name="request"/>.DestinationTable against the fields flattened out of
    /// <paramref name="request"/>.SourceJson, returning one ranked suggestion per column. Nothing is persisted —
    /// call <see cref="ApproveMappings"/> once a user confirms which suggestions to keep.
    /// </summary>
    [HttpPost("suggest")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [ProducesResponseType(typeof(List<FieldMappingSuggestion>), StatusCodes.Status200OK)]
    public async Task<IActionResult> SuggestMappings(
        [FromBody] SuggestSchemaMappingsRequest request, CancellationToken cancellationToken)
    {
        var destinationFields = request.DestinationFields ?? await ResolveDestinationFieldsAsync(
            request.DestinationId, request.DestinationTableName, cancellationToken);

        using var sourceJson = JsonDocument.Parse(request.SourceJson.GetRawText());
        var suggestions = await _schemaMatchingService.SuggestMappingsAsync(
            request.SourceSystem,
            request.ResourceType,
            request.DestinationTableName,
            sourceJson,
            destinationFields,
            cancellationToken);

        return Ok(suggestions);
    }

    /// <summary>Persists one or more user-approved (or rejected) field matches — future suggest calls for the
    /// same destination field reuse the approved match at confidence 1.0 instead of re-scoring it.</summary>
    [HttpPost("approve")]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    [StandardPermission(
        PermissionGroupCode.Configuration,
        PermissionActionCode.Write,
        description: "Approve or reject a suggested schema field mapping.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> ApproveMappings(
        [FromBody] ApproveSchemaMappingsRequest request, CancellationToken cancellationToken)
    {
        await _schemaMatchingService.SaveApprovedMappingAsync(
            request.SourceSystem,
            request.ResourceType,
            request.DestinationTableName,
            request.Mappings,
            cancellationToken);

        return NoContent();
    }

    /// <summary>Every currently-approved field mapping for one (source system, resource type, destination table).</summary>
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.UnifiedAdmin)]
    public async Task<IActionResult> GetApprovedMappings(
        [FromQuery] string sourceSystem,
        [FromQuery] string resourceType,
        [FromQuery] string destinationTableName,
        CancellationToken cancellationToken)
    {
        var mappings = await _schemaMatchingService.GetApprovedMappingsAsync(
            sourceSystem, resourceType, destinationTableName, cancellationToken);

        return Ok(mappings);
    }

    private async Task<List<FhirFieldMetadata>> ResolveDestinationFieldsAsync(
        Guid? destinationId, string destinationTableName, CancellationToken cancellationToken)
    {
        if (destinationId is null)
        {
            return [];
        }

        var schema = await _destinationSchemaService.GetSchemaAsync(destinationId.Value, cancellationToken);
        var table = schema.Tables.FirstOrDefault(
            t => string.Equals(t.TableName, destinationTableName, StringComparison.OrdinalIgnoreCase));

        return table is null
            ? []
            : table.Columns.Select(FhirFieldMetadata.FromDestinationColumn).ToList();
    }
}

/// <summary>
/// <see cref="DestinationFields"/> is optional — when omitted (and <see cref="DestinationId"/> is supplied),
/// the controller resolves the live column list via <see cref="IDestinationSchemaService"/> so callers don't
/// have to hand-build metadata for an already-registered destination.
/// </summary>
public sealed record SuggestSchemaMappingsRequest(
    string SourceSystem,
    string ResourceType,
    string DestinationTableName,
    JsonElement SourceJson,
    Guid? DestinationId = null,
    List<FhirFieldMetadata>? DestinationFields = null);

public sealed record ApproveSchemaMappingsRequest(
    string SourceSystem,
    string ResourceType,
    string DestinationTableName,
    IReadOnlyList<ApprovedMappingRequest> Mappings);
