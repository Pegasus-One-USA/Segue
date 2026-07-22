using System.Text.Json;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Imports a mapping configuration posted by the Workflow Builder's "Update" button: persists one
/// <see cref="Domain.Entities.MappingProfile"/> per resourceType and applies the destination schema changes
/// (new tables/columns) it implies. See <c>MappingImport-API-Spec.md</c> for the full contract.
/// </summary>
public interface IMappingImportService
{
    /// <summary>
    /// <paramref name="request"/> is accepted as a raw <see cref="JsonElement"/> (rather than a typed DTO) so
    /// each resourceType's exact, unmodified source JSON can be captured verbatim into
    /// <see cref="Domain.Entities.MappingProfile.MappingJson"/> — round-tripping through the permissive typed
    /// DTOs would silently drop any property they don't model.
    /// </summary>
    Task<MappingImportResultDto> ImportAsync(JsonElement request, CancellationToken cancellationToken);
}
