using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Default materializer: projects the engine result into a parent table + child tables.
/// A concrete persistence writer (SQL bulk insert) can wrap or replace this; the shape it
/// produces (parent rows + child tables with RowIndex) is what gets written with FK/ordinal.
/// </summary>
public sealed class DefaultMappingMaterializer : IMappingMaterializer
{
    public MaterializedDataset Materialize(string parentTable, MappingTestResultDto mappingResult)
    {
        var parentRows = mappingResult.Rows ?? [mappingResult.Values];
        var childTables = mappingResult.ChildTables ?? [];
        return new MaterializedDataset(parentTable, parentRows, childTables);
    }
}
