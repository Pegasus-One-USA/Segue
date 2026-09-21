using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Data Lineage: source field → mapping rule → destination column → export, for one resource within a route
/// execution. <see cref="GetLineageAsync"/> returns structure only (JsonPaths/rule names/column names — metadata
/// about the mapping, not PHI) and is safe for anyone with governance.read. There is no value-reveal counterpart
/// any more: the fetched/normalized/mapped resource content those values came from is no longer stored.
/// </summary>
public interface IDataLineageService
{
    Task<DataLineageDto?> GetLineageAsync(Guid resourceRecordId, CancellationToken cancellationToken);
}
