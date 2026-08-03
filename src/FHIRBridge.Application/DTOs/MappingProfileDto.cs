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
    Guid? SourceConfigurationId = null,
    /// <summary>Set only by <c>MappingImportService</c> (the Mapping Config Import wizard). Null for a profile
    /// created/updated via the simpler workflow-build path (<c>ConfigurationService.AddMappingProfileAsync</c>)
    /// — the reliable signal for "this profile's JsonPaths were properly derived, don't overwrite them."</summary>
    string? MappingJson = null);
