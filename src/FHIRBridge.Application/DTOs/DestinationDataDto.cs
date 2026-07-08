namespace FHIRBridge.Application.DTOs;

/// <summary>A capped sample of rows read back from a relational destination table (portal "View destination data").</summary>
public sealed record DestinationDataDto(
    string Table,
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<string?>> Rows,
    int RowCount,
    string? Error);
