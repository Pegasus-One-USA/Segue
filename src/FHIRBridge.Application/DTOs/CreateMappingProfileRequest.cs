namespace FHIRBridge.Application.DTOs;

public sealed record CreateMappingProfileRequest(
    string Name,
    string ResourceType,
    Guid SourceConnectionId,
    Guid DestinationId,
    string DestinationObject,
    IReadOnlyList<MappingFieldDto> Fields,
    // Which reusable SourceConnection's workflow-specific retrieval/scopes config this mapping uses. Null means the
    // caller doesn't know about this concept yet (today's portal) — the service auto-provisions one from the
    // connection's current settings on create, and reuses the mapping's existing configuration on update.
    Guid? SourceConfigurationId = null);

/// <summary>"Mark as Master" request body — the name to give the new, independently-owned master mapping
/// profile cloned from a workflow's own mapping (see IConfigurationService.PromoteMappingProfileToMasterAsync).</summary>
public sealed record PromoteMappingProfileRequest(string Name);
