using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Abstractions.Mapping;

/// <summary>
/// Turns an engine <see cref="MappingTestResultDto"/> into a destination-shaped dataset:
/// a parent table plus zero or more child tables (from SeparateDestination fields). This is the
/// seam a concrete SQL writer plugs into to persist parent rows + child rows with FK/ordinal index.
/// </summary>
public interface IMappingMaterializer
{
    MaterializedDataset Materialize(string parentTable, MappingTestResultDto mappingResult);
}

public sealed record MaterializedDataset(
    string ParentTable,
    IReadOnlyList<IReadOnlyDictionary<string, object?>> ParentRows,
    IReadOnlyList<MappingChildTableDto> ChildTables);
