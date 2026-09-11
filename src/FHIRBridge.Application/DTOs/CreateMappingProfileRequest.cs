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
    Guid? SourceConfigurationId = null,
    // Which workflow this mapping is being saved from, when the caller knows. Validation only: transformation
    // rules authored in the V2 builder are Workflow-scoped, so without this the validator resolves rules at the
    // tenant-wide tiers alone and never sees the rule that is actually going to transform the column — it then
    // compares the RAW extraction type against the column and rejects a perfectly valid rule-backed mapping.
    // Null from a caller that has no workflow context, and from a workflow still unsaved (whose rules are found
    // via the pending tier instead).
    Guid? ResourcePipelineRouteId = null);

/// <summary>"Mark as Master" request body — the name to give the new, independently-owned master mapping
/// profile cloned from a workflow's own mapping (see IConfigurationService.PromoteMappingProfileToMasterAsync).</summary>
public sealed record PromoteMappingProfileRequest(string Name);
