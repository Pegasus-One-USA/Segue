using FHIRBridge.Api.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Api.Workflows;

public static class WorkflowEndpoints
{
    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1")
            .WithTags("Workflows");

        group.MapGet("/workflow-catalog", (IWorkflowNodeCatalog catalog) => Results.Ok(catalog.List()));

        group.MapPost("/workflows", async (
            WorkflowDefinitionRequest request,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = BuildWorkflow(Guid.NewGuid(), request);
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Created($"/api/v1/workflows/{workflow.Id}", workflow);
        });

        group.MapGet("/workflows", async (
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(cancellationToken)));

        group.MapGet("/workflows/{workflowId:guid}", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            return workflow is null ? Results.NotFound() : Results.Ok(workflow);
        });

        group.MapPut("/workflows/{workflowId:guid}", async (
            Guid workflowId,
            WorkflowDefinitionRequest request,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = BuildWorkflow(workflowId, request);
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Ok(workflow);
        });

        group.MapPost("/workflows/{workflowId:guid}/validate", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            IWorkflowGraphValidator validator,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var result = validator.Validate(workflow);
            return Results.Ok(result);
        });

        group.MapPost("/workflows/{workflowId:guid}/run", async (
            Guid workflowId,
            WorkflowRunRequest? request,
            IWorkflowDefinitionStore store,
            IRankedWorkflowOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var context = new WorkflowExecutionContext(
                Guid.NewGuid(),
                request?.CorrelationId ?? Guid.NewGuid().ToString("N"));
            var result = await orchestrator.ExecuteAsync(workflow, context, cancellationToken);

            return Results.Ok(result);
        });

        group.MapPost("/workflows/{workflowId:guid}/activate", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            workflow.Activate();
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Ok(workflow);
        });

        group.MapPost("/workflows/{workflowId:guid}/deactivate", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            workflow.Deactivate();
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Ok(workflow);
        });

        return endpoints;
    }

    private static WorkflowDefinition BuildWorkflow(Guid workflowId, WorkflowDefinitionRequest request)
    {
        var workflow = new WorkflowDefinition(workflowId, request.Name, version: 1, request.IsEnabled);
        var nodeIdsByClientId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var nodeRequest in request.Nodes)
        {
            var node = workflow.AddNode(
                nodeRequest.NodeType,
                nodeRequest.Category,
                nodeRequest.Rank,
                nodeRequest.SubRank,
                nodeRequest.DisplayName,
                nodeRequest.ConfigurationJson ?? "{}",
                nodeRequest.PositionX,
                nodeRequest.PositionY,
                nodeRequest.IsEnabled);

            nodeIdsByClientId[nodeRequest.Id] = node.Id;
        }

        foreach (var edgeRequest in request.Edges)
        {
            if (nodeIdsByClientId.TryGetValue(edgeRequest.FromNodeId, out var fromNodeId)
                && nodeIdsByClientId.TryGetValue(edgeRequest.ToNodeId, out var toNodeId))
            {
                workflow.AddEdge(fromNodeId, toNodeId);
            }
        }

        return workflow;
    }
}
