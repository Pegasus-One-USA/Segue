using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Api.Workflows;

/// <summary>
/// Option B "create-on-save": build a workflow AND the real Source / Destination / Mapping records it references, in a
/// single call. The client sends the canvas graph (same shape as <see cref="WorkflowDefinitionRequest"/>) plus
/// create-specs tagged with the client node id they belong to. The endpoint provisions any inline secret, creates each
/// entity, injects the resulting ids into the matching node's configuration (source → <c>sourceConnectionId</c>,
/// mapping → <c>mappingProfileId</c>, destination → secret reference + <c>target</c> + <c>destinationId</c>), then saves
/// the graph so the persisted workflow resolves real config by id at run / launch time.
/// </summary>
/// <param name="WorkflowId">
/// When set (re-saving an already-built workflow), the existing workflow definition is updated in place instead of
/// creating a new one — paired with each spec's <c>ExistingId</c>, this makes the whole endpoint idempotent for
/// edits: nothing is duplicated, and changes to retrieval/connection config actually reach the backing
/// Source/Destination/MappingProfile records instead of only updating the node's display config.
/// </param>
public sealed record WorkflowBuildRequest(
    string Name,
    bool IsEnabled,
    IReadOnlyCollection<WorkflowNodeRequest> Nodes,
    IReadOnlyCollection<WorkflowEdgeRequest> Edges,
    IReadOnlyCollection<SourceBuildSpec>? Sources = null,
    IReadOnlyCollection<DestinationBuildSpec>? Destinations = null,
    IReadOnlyCollection<MappingBuildSpec>? Mappings = null,
    WorkflowTriggerRequest? Trigger = null,
    Guid? WorkflowId = null);

/// <summary>
/// Create a source connection and inject its id into the node identified by <see cref="NodeId"/>. When
/// <see cref="ExistingId"/> is set (the node already carries a <c>sourceConnectionId</c> from a prior create-on-save,
/// round-tripped through the client's node fields on edit) the existing record is updated in place instead.
/// </summary>
public sealed record SourceBuildSpec(string NodeId, CreateSourceConnectionRequest Source, Guid? ExistingId = null);

/// <summary>
/// Create a destination configuration (provisioning <see cref="CreateDestinationConfigurationRequest.InlineSecret"/>
/// when present) and inject its secret reference + target + id into the node identified by <see cref="NodeId"/>.
/// When <see cref="ExistingId"/> is set, updates that destination in place instead of creating a new one.
/// </summary>
public sealed record DestinationBuildSpec(
    string NodeId,
    CreateDestinationConfigurationRequest Destination,
    Guid? ExistingId = null);

/// <summary>
/// Create a mapping profile bound to a source and destination created (or picked) elsewhere in this request. The
/// source/destination are referenced by their canvas node ids so the mapping can be wired before the entities have
/// server-assigned ids; each resolves to a just-created entity, or falls back to a <c>sourceConnectionId</c> /
/// <c>destinationId</c> already present on the referenced node's config (picker flow). When
/// <see cref="ExistingId"/> is set, updates that mapping profile in place instead of creating a new one.
/// </summary>
public sealed record MappingBuildSpec(
    string NodeId,
    string SourceNodeId,
    string DestinationNodeId,
    string Name,
    string ResourceType,
    string DestinationObject,
    IReadOnlyList<MappingFieldDto> Fields,
    Guid? ExistingId = null);

/// <summary>Ids of everything created, keyed by the canvas node id each entity was attached to.</summary>
public sealed record WorkflowBuildResult(
    Guid WorkflowId,
    IReadOnlyDictionary<string, Guid> SourceConnectionIds,
    IReadOnlyDictionary<string, Guid> DestinationIds,
    IReadOnlyDictionary<string, Guid> MappingProfileIds);
