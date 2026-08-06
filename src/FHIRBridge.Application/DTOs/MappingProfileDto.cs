namespace FHIRBridge.Application.DTOs;

public sealed record MappingProfileDto(
    Guid Id,
    string Name,
    string ResourceType,
    Guid SourceConnectionId,
    Guid DestinationId,
    string DestinationObject,
    IReadOnlyList<MappingFieldDto> Fields,
    bool IsEnabled,
    DateTime CreatedOnUtc,
    string? CreatedBy,
    DateTime? ModifiedOnUtc,
    string? ModifiedBy,
    Guid? SourceConfigurationId = null,
    string? MappingJson = null,
    Guid? WorkflowId = null);
