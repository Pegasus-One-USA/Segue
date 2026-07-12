using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Api.Workflows;

public sealed record WorkflowDefinitionRequest(
    string Name,
    bool IsEnabled,
    IReadOnlyCollection<WorkflowNodeRequest> Nodes,
    IReadOnlyCollection<WorkflowEdgeRequest> Edges,
    WorkflowTriggerRequest? Trigger = null);

/// <summary>Optional workflow-level scheduling metadata (Backend-Systems workflows). Omit / Manual = run on demand.</summary>
public sealed record WorkflowTriggerRequest(
    WorkflowTriggerType Type,
    string? ScheduleExpression = null,
    int? IntervalMinutes = null,
    bool BackfillOnFirstRun = false);

public sealed record WorkflowNodeRequest(
    string Id,
    string NodeType,
    WorkflowNodeCategory Category,
    int Rank,
    int SubRank,
    string? DisplayName,
    string? ConfigurationJson,
    double PositionX,
    double PositionY,
    bool IsEnabled,
    bool CheckpointUrlEnabled = false);

public sealed record WorkflowEdgeRequest(string FromNodeId, string ToNodeId);

public sealed record WorkflowRunRequest(string? CorrelationId);

public sealed record CopyWorkflowRequest(string Name);
