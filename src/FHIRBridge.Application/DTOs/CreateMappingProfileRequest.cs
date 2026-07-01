namespace FHIRBridge.Application.DTOs;

public sealed record CreateMappingProfileRequest(
    string Name,
    string ResourceType,
    Guid SourceConnectionId,
    Guid DestinationId,
    string DestinationObject,
    IReadOnlyList<MappingFieldDto> Fields);
