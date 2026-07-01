namespace FHIRBridge.Application.DTOs;

public sealed record DestinationSchemaDto(
    Guid DestinationId,
    IReadOnlyList<DestinationTableSchemaDto> Tables);

public sealed record DestinationTableSchemaDto(
    string SchemaName,
    string TableName,
    string FullName,
    IReadOnlyList<DestinationColumnSchemaDto> Columns);

public sealed record DestinationColumnSchemaDto(
    string Name,
    string DataType,
    string MappingValueType,
    bool IsNullable,
    int? MaxLength);
