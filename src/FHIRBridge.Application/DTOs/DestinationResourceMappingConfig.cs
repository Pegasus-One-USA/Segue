namespace FHIRBridge.Application.DTOs;

/// <summary>
/// One resource type's write shape on a destination node that handles more than one resource type (e.g. a SQL
/// Server destination receiving Patient, Condition, and Observation, each going to its own table). Keyed by
/// resource type on the destination node's <c>resourceMappings</c> config property so the destination executor can
/// route each resource type's records to its own target/columns instead of forcing every resource type through
/// whichever one happened to be configured first.
/// </summary>
public sealed record DestinationResourceMappingConfig(
    string DestinationObject,
    IReadOnlyList<MappingFieldDto> Fields);
