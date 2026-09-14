using FHIRBridge.Application.Security;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.AspNetCore.Authorization;

namespace FHIRBridge.Api.Workflows;

/// <summary>
/// Incremental per-node mutations against an already-created workflow (Step 2 of the Workflow V3 plan: "Add to
/// Workflow" persists the node immediately instead of only updating local canvas state). Deliberately separate
/// from <see cref="WorkflowEndpoints"/>'s whole-graph <c>/workflows/build</c> and <c>PUT /workflows/{id}</c>,
/// which provision sources/destinations/mappings and validate a COMPLETE graph — these endpoints do neither:
/// they persist one node (or one edge) at a time against a workflow that may legitimately be mid-build (e.g.
/// only a source node so far, no destination yet). Full structural validation stays exclusively at
/// <c>/workflows/build</c> (final save) and <c>/workflows/validate</c> (preview).
/// </summary>
public static class WorkflowNodeEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowNodeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1")
            .WithTags("Workflows");

        // Adds one node (and, optionally, one edge from an existing node into it) to an already-created
        // workflow. The node's own persisted id is minted here and returned — the caller (the portal canvas)
        // must hold onto it to reference this node in a later PUT/DELETE or edge add, since node identity is
        // otherwise only stable within a single request (see WorkflowDefinition.AddNode's id parameter).
        group.MapPost("/workflows/{workflowId:guid}/nodes", async (
            Guid workflowId,
            AddWorkflowNodeRequest request,
            IWorkflowDefinitionStore store,
            IWorkflowNodeCatalog catalog,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            if (catalog.Find(request.NodeType) is null)
            {
                return Results.BadRequest(new { error = $"Unknown node type '{request.NodeType}'." });
            }

            if (request.FromNodeId is { } fromNodeId && workflow.Nodes.All(node => node.Id != fromNodeId))
            {
                return Results.BadRequest(new { error = $"Edge source node '{fromNodeId}' does not exist on this workflow." });
            }

            var node = workflow.AddNode(
                request.NodeType,
                request.Category,
                request.Rank,
                request.SubRank,
                request.DisplayName,
                request.ConfigurationJson ?? "{}",
                request.PositionX,
                request.PositionY,
                request.IsEnabled,
                request.CheckpointUrlEnabled);

            if (request.FromNodeId is { } edgeFromNodeId)
            {
                workflow.AddEdge(edgeFromNodeId, node.Id);
            }

            workflow.BumpVersion();
            await store.SaveAsync(workflow, cancellationToken);

            return Results.Created($"/api/v1/workflows/{workflowId}/nodes/{node.Id}", ToResponse(node, workflow.Version));
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Edit)));

        // Replaces one existing node's editable fields in place. The node keeps its id — everything
        // referencing it (edges, the node's own configuration) is unaffected.
        group.MapPut("/workflows/{workflowId:guid}/nodes/{nodeId:guid}", async (
            Guid workflowId,
            Guid nodeId,
            UpdateWorkflowNodeRequest request,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var existingNode = workflow.Nodes.SingleOrDefault(node => node.Id == nodeId);
            if (existingNode is null)
            {
                return Results.NotFound();
            }

            workflow.RemoveNode(nodeId);
            var updatedNode = workflow.AddNode(
                existingNode.NodeType,
                existingNode.Category,
                existingNode.Rank,
                existingNode.SubRank,
                request.DisplayName ?? existingNode.DisplayName,
                request.ConfigurationJson ?? existingNode.ConfigurationJson,
                request.PositionX ?? existingNode.PositionX,
                request.PositionY ?? existingNode.PositionY,
                request.IsEnabled ?? existingNode.IsEnabled,
                request.CheckpointUrlEnabled ?? existingNode.CheckpointUrlEnabled,
                nodeId);

            workflow.BumpVersion();
            await store.SaveAsync(workflow, cancellationToken);

            return Results.Ok(ToResponse(updatedNode, workflow.Version));
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Edit)));

        // Removes one node (and any edge touching it — see WorkflowDefinition.RemoveNode). Does not attempt the
        // source-connection/destination delete-permission check /workflows/build and PUT /workflows/{id} run,
        // since it targets the workflow node only, not the underlying SourceConnection/DestinationConfiguration
        // row those endpoints also retire; that cleanup pass stays a whole-graph concern for now.
        group.MapDelete("/workflows/{workflowId:guid}/nodes/{nodeId:guid}", async (
            Guid workflowId,
            Guid nodeId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            if (workflow.Nodes.All(node => node.Id != nodeId))
            {
                return Results.NotFound();
            }

            workflow.RemoveNode(nodeId);
            workflow.BumpVersion();
            await store.SaveAsync(workflow, cancellationToken);

            return Results.NoContent();
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Edit)));

        return endpoints;
    }

    private static WorkflowNodeResponse ToResponse(WorkflowNode node, int workflowVersion) => new(
        node.Id,
        node.NodeType,
        node.Category,
        node.Rank,
        node.SubRank,
        node.DisplayName,
        node.ConfigurationJson,
        node.PositionX,
        node.PositionY,
        node.IsEnabled,
        node.CheckpointUrlEnabled,
        workflowVersion);
}

/// <summary>
/// <paramref name="FromNodeId"/>, when present, also adds an edge from that existing node into the new one —
/// the common "drag a connector from the node I just dropped" case in one round trip instead of two.
/// </summary>
public sealed record AddWorkflowNodeRequest(
    string NodeType,
    WorkflowNodeCategory Category,
    int Rank,
    int SubRank = 0,
    string? DisplayName = null,
    string? ConfigurationJson = null,
    double PositionX = 0,
    double PositionY = 0,
    bool IsEnabled = true,
    bool CheckpointUrlEnabled = false,
    Guid? FromNodeId = null);

/// <summary>Every field is optional — omitted fields keep the node's current value.</summary>
public sealed record UpdateWorkflowNodeRequest(
    string? DisplayName = null,
    string? ConfigurationJson = null,
    double? PositionX = null,
    double? PositionY = null,
    bool? IsEnabled = null,
    bool? CheckpointUrlEnabled = null);

/// <summary><paramref name="WorkflowVersion"/> is the workflow's version AFTER this call's save — the caller's
/// next mutation of this workflow must be built against a definition read at (or after) this version.</summary>
public sealed record WorkflowNodeResponse(
    Guid Id,
    string NodeType,
    WorkflowNodeCategory Category,
    int Rank,
    int SubRank,
    string DisplayName,
    string ConfigurationJson,
    double PositionX,
    double PositionY,
    bool IsEnabled,
    bool CheckpointUrlEnabled,
    int WorkflowVersion);
