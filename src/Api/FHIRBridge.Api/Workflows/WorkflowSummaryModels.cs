namespace FHIRBridge.Api.Workflows;

/// <summary>
/// One row of the workflow-list screen: the graph's shape (node/edge counts), its enabled state, the most recent run,
/// and — derived from the source node's referenced connection's <c>ApplicationType</c> — whether the workflow is
/// <c>Launch</c>ed (interactive SMART: EHR launch / standalone / patient) or <c>Run</c> (backend / non-interactive),
/// with the endpoint the UI should call for that action.
/// </summary>
public sealed record WorkflowSummaryDto(
    Guid WorkflowId,
    string Name,
    string Status,
    int Nodes,
    int Edges,
    string? LastRun,
    DateTimeOffset? LastRunAt,
    string Action,
    string ActionEndpoint,
    Guid? SourceConnectionId,
    string? SourceSystemType,
    string? ApplicationType,
    bool HasDestination,
    bool IsPubliclyLaunchable);
