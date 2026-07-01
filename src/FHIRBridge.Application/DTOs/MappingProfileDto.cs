namespace FHIRBridge.Application.DTOs;

public sealed record MappingProfileDto(
    Guid Id,
    string Name,
    string ResourceType,
    Guid SourceConnectionId,
    Guid DestinationId,
    string DestinationObject,
    IReadOnlyList<MappingFieldDto> Fields,
    bool IsEnabled);
