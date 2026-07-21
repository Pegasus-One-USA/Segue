using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

public sealed record CreateResourceRouteRequest(
    IngestionMode IngestionMode,
    Guid? WebhookConfigurationId,
    Guid MappingProfileId,
    string? ScheduleExpression,
    string? SearchParameters,
    bool IsEnabled,
    int Priority,
    IReadOnlyList<ResourceRouteMappingRequest>? ResourceMappings = null);

public sealed record ResourceRouteMappingRequest(
    Guid MappingProfileId,
    bool IsEnabled,
    int ExecutionOrder,
    string? SearchParameters = null,
    IReadOnlyList<ParentReferenceRequest>? ParentReferences = null);

/// <summary>
/// Declares that this resource mapping is a "child" of another mapping (<see cref="ParentMappingProfileId"/>)
/// in the same route. A mapping can have several — e.g. Observation can be a child of both Patient and
/// Encounter at once, each independently requiring its own reference field to be mapped.
/// </summary>
public sealed record ParentReferenceRequest(
    Guid ParentMappingProfileId,
    string? ReferenceFieldOverride = null);
