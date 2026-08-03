using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Data Lineage: source field → mapping rule → destination column → export, for one resource within a route
/// execution. <see cref="GetLineageAsync"/> returns structure only (JsonPaths/rule names/column names — metadata
/// about the mapping, not PHI) and is safe for anyone with governance.read; it never touches the encrypted
/// FetchedJson/NormalizedJson/MappedValuesJson columns. <see cref="RevealFieldValueAsync"/> is the one path that
/// decrypts a single field's actual value — callers must gate it behind a stricter permission (payload.view) and
/// must themselves write the DataAccessLog entry for the reveal (this service does not log on its own, since the
/// caller has the ambient actor/correlation context).
/// </summary>
public interface IDataLineageService
{
    Task<DataLineageDto?> GetLineageAsync(Guid resourceRecordId, CancellationToken cancellationToken);

    Task<LineageFieldValueDto> RevealFieldValueAsync(
        Guid resourceRecordId, string targetField, CancellationToken cancellationToken);
}
