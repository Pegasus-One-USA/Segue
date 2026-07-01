using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Api.Workflows;

public sealed record WorkflowDefinitionRequest(
    string Name,
    bool IsEnabled,
    IReadOnlyCollection<WorkflowNodeRequest> Nodes,
    IReadOnlyCollection<WorkflowEdgeRequest> Edges);

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
    bool IsEnabled);

public sealed record WorkflowEdgeRequest(string FromNodeId, string ToNodeId);

public sealed record WorkflowRunRequest(string? CorrelationId);
