namespace FHIRBridge.Application.DTOs;

public sealed record TestMappingRequest(
    string SourceJson,
    IReadOnlyList<MappingFieldDto> Fields);
