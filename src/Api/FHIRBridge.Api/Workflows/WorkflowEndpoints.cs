using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Enums;

namespace FHIRBridge.Api.Workflows;

public static class WorkflowEndpoints
{
    // Node executors read config with JsonSerializerDefaults.Web (camelCase); serialize embedded fields the same way.
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

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

        // Option B create-on-save: provision secrets + create Source/Destination/Mapping records, inject their ids into
        // the referencing nodes, then persist the graph. One call turns a builder canvas into a launchable workflow that
        // resolves real, RBAC-scoped configuration by id at run time. Gated to admins because it provisions secrets.
        group.MapPost("/workflows/build", async (
            WorkflowBuildRequest request,
            IConfigurationService configurationService,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            // Working copy of the nodes keyed by client id; created-entity ids are injected here so they ride into the
            // saved graph. Node order is preserved from the original request when the definition is rebuilt.
            var nodes = request.Nodes.ToDictionary(node => node.Id, node => node, StringComparer.OrdinalIgnoreCase);
            var sourceIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var destinationIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
            var mappingIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

            // 1. Destinations first — self-contained, and they provision the inline secret whose reference the node needs.
            foreach (var spec in request.Destinations ?? [])
            {
                if (!nodes.TryGetValue(spec.NodeId, out var node))
                {
                    return Results.BadRequest($"Destination spec references unknown node '{spec.NodeId}'.");
                }

                var destination = spec.ExistingId is { } existingDestinationId
                    ? await configurationService.UpdateDestinationConfigurationAsync(existingDestinationId, spec.Destination, cancellationToken)
                    : await configurationService.AddDestinationConfigurationAsync(spec.Destination, cancellationToken);
                destinationIds[spec.NodeId] = destination.Id;
                nodes[spec.NodeId] = WithConfiguration(node, config =>
                {
                    config["destinationId"] = destination.Id.ToString();
                    config["secretKeyVaultName"] = destination.KeyVaultName;
                    config["secretName"] = destination.SecretName;
                    if (!string.IsNullOrWhiteSpace(destination.Target))
                    {
                        config["target"] = destination.Target;
                    }
                });
            }

            // 2. Sources — the executor resolves base URL + auth + token live from the id at run time.
            foreach (var spec in request.Sources ?? [])
            {
                if (!nodes.TryGetValue(spec.NodeId, out var node))
                {
                    return Results.BadRequest($"Source spec references unknown node '{spec.NodeId}'.");
                }

                var source = spec.ExistingId is { } existingSourceId
                    ? await configurationService.UpdateSourceConnectionAsync(existingSourceId, spec.Source, cancellationToken)
                    : await configurationService.AddSourceConnectionAsync(spec.Source, cancellationToken);
                sourceIds[spec.NodeId] = source.Id;
                nodes[spec.NodeId] = WithConfiguration(node, config => config["sourceConnectionId"] = source.Id.ToString());
            }

            // 3. Mappings — bound to a source + destination created above (or already referenced on the picked node).
            foreach (var spec in request.Mappings ?? [])
            {
                if (!nodes.TryGetValue(spec.NodeId, out var node))
                {
                    return Results.BadRequest($"Mapping spec references unknown node '{spec.NodeId}'.");
                }

                if (!TryResolveEntityId(spec.SourceNodeId, sourceIds, nodes, "sourceConnectionId", out var sourceConnectionId))
                {
                    return Results.BadRequest(
                        $"Mapping spec '{spec.NodeId}' references source node '{spec.SourceNodeId}' with no created or referenced source connection.");
                }

                if (!TryResolveEntityId(spec.DestinationNodeId, destinationIds, nodes, "destinationId", out var destinationId))
                {
                    return Results.BadRequest(
                        $"Mapping spec '{spec.NodeId}' references destination node '{spec.DestinationNodeId}' with no created or referenced destination.");
                }

                var mappingRequest = new CreateMappingProfileRequest(
                    spec.Name,
                    spec.ResourceType,
                    sourceConnectionId,
                    destinationId,
                    spec.DestinationObject,
                    spec.Fields);
                var mapping = spec.ExistingId is { } existingMappingId
                    ? await configurationService.UpdateMappingProfileAsync(existingMappingId, mappingRequest, cancellationToken)
                    : await configurationService.AddMappingProfileAsync(mappingRequest, cancellationToken);
                mappingIds[spec.NodeId] = mapping.Id;
                nodes[spec.NodeId] = WithConfiguration(node, config => config["mappingProfileId"] = mapping.Id.ToString());

                // The destination executor rebuilds its write-time mapping (target table + the columns it auto-creates)
                // from its OWN node config rather than resolving the mapping by id, so mirror the mapping's target and
                // fields onto the destination node — the same shape the route→graph projection embeds. Without this the
                // writer defaults the target table to the resource type and creates no data columns.
                if (nodes.TryGetValue(spec.DestinationNodeId, out var destinationNode))
                {
                    nodes[spec.DestinationNodeId] = WithConfiguration(destinationNode, config =>
                    {
                        config["resourceType"] = spec.ResourceType;
                        config["destinationObject"] = spec.DestinationObject;
                        config["fields"] = JsonSerializer.SerializeToNode(spec.Fields, WebJsonOptions);
                    });
                }
            }

            // 4. Persist the graph carrying the injected references (original node order preserved).
            var definitionRequest = new WorkflowDefinitionRequest(
                request.Name,
                request.IsEnabled,
                request.Nodes.Select(node => nodes[node.Id]).ToArray(),
                request.Edges,
                request.Trigger);

            var workflow = BuildWorkflow(request.WorkflowId ?? Guid.NewGuid(), definitionRequest);
            await store.SaveAsync(workflow, cancellationToken);

            var result = new WorkflowBuildResult(workflow.Id, sourceIds, destinationIds, mappingIds);
            return Results.Created($"/api/v1/workflows/{workflow.Id}", result);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        group.MapPost("/workflows/validate", (
            WorkflowDefinitionRequest request,
            IWorkflowGraphValidator validator) =>
        {
            var workflow = BuildWorkflow(Guid.NewGuid(), request);
            var result = validator.Validate(workflow);
            return Results.Ok(result);
        });

        group.MapGet("/workflows", async (
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(cancellationToken)));

        // Workflow-list screen: one summary row per workflow — shape, enabled state, last run, and the derived
        // action. The source node's referenced connection decides Launch (interactive SMART) vs Run (backend), so the
        // UI knows which endpoint to call. Admin-only (it reads source-connection configuration).
        group.MapGet("/workflows/summary", async (
            IWorkflowDefinitionStore store,
            IWorkflowRunStore runStore,
            IConfigurationRepository configurationRepository,
            CancellationToken cancellationToken) =>
        {
            var workflows = await store.ListAsync(cancellationToken);
            var sources = await configurationRepository.GetSourceConnectionsAsync(cancellationToken);
            var applicationTypeBySourceId = sources.ToDictionary(source => source.Id, source => source.ApplicationType);
            var systemTypeBySourceId = sources.ToDictionary(source => source.Id, source => source.SourceSystemType);

            var summaries = new List<WorkflowSummaryDto>(workflows.Count);
            foreach (var workflow in workflows)
            {
                // Walk the source nodes → their referenced connections. Mirror the SQL's MAX(ApplicationType): the
                // highest-precedence interactive type wins, so a workflow with any EHR-launch/standalone/patient
                // source is launched rather than run.
                Guid? firstSourceId = null;
                Guid? launchSourceId = null;
                ApplicationType? applicationType = null;
                foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Source))
                {
                    if (!TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceId))
                    {
                        continue;
                    }

                    firstSourceId ??= sourceId;
                    if (applicationTypeBySourceId.TryGetValue(sourceId, out var type) && type is not null
                        && (applicationType is null || type.Value > applicationType.Value))
                    {
                        applicationType = type;
                        launchSourceId = sourceId;
                    }
                }

                var isLaunch = applicationType is ApplicationType.EhrLaunch or ApplicationType.Standalone or ApplicationType.Patient;

                var hasDestination = workflow.Nodes.Any(node =>
                    node.Category == WorkflowNodeCategory.Destination
                    && TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out _));

                var runs = await runStore.ListByDefinitionAsync(workflow.Id, cancellationToken);
                var lastRun = runs.OrderByDescending(run => run.StartedAt).FirstOrDefault();

                var resolvedSourceId = launchSourceId ?? firstSourceId;
                var sourceSystemType = resolvedSourceId is { } id && systemTypeBySourceId.TryGetValue(id, out var systemType)
                    ? systemType.ToString()
                    : null;

                summaries.Add(new WorkflowSummaryDto(
                    workflow.Id,
                    workflow.Name,
                    workflow.IsEnabled ? "Enabled" : "Disabled",
                    workflow.Nodes.Count,
                    workflow.Edges.Count,
                    lastRun?.Status.ToString(),
                    lastRun?.StartedAt,
                    isLaunch ? "Launch" : "Run",
                    isLaunch
                        ? $"/api/v1/workflows/{workflow.Id}/launch-url"
                        : $"/api/v1/workflows/{workflow.Id}/run",
                    resolvedSourceId,
                    sourceSystemType,
                    applicationType?.ToString(),
                    hasDestination,
                    workflow.IsPubliclyLaunchable));
            }

            return Results.Ok(summaries
                .OrderBy(summary => summary.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // "View destination data": resolve the workflow's destination node → its created destination + target table
        // and read back a capped row sample so the UI can show what the pipeline wrote. Admin-only.
        group.MapGet("/workflows/{workflowId:guid}/destination-data", async (
            Guid workflowId,
            int? top,
            IWorkflowDefinitionStore store,
            IConfigurationRepository configurationRepository,
            IDestinationDataService destinationDataService,
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var (destinationId, destinationObject) = await ResolveDestinationTargetAsync(
                workflow, configurationRepository, cancellationToken);

            if (destinationId == Guid.Empty)
            {
                return Results.Ok(new DestinationDataDto(
                    string.Empty, [], [], 0,
                    "This workflow has no saved destination to preview. Save or build the destination first."));
            }

            var runs = await runStore.ListByDefinitionAsync(workflowId, cancellationToken);
            var pipelineRunIds = runs.Select(run => run.Id).ToArray();

            var result = await destinationDataService.ReadSampleAsync(
                destinationId, destinationObject, top ?? 50, pipelineRunIds, cancellationToken);
            return Results.Ok(result);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Third-party-app return trip for a workflow-triggered EHR launch (see OAuthController.Callback, which
        // redirects here with ?workflowRunId=... once the launch's workflow run completes). Anonymous — the calling
        // app has no FHIRBridge session; the unguessable run id is the trust boundary, same as /oauth/callback.
        // Demo-scoped: reads the source node's raw retrieved FHIR resources straight from Execution History
        // (the same decrypted payload the admin "Execution History" screen shows) rather than any destination
        // table, and hands back the Patient resource's own JSON as-is for the caller to bind directly. This is
        // independent of whatever destination tables/Mapping Profiles exist — it reflects what the source node
        // actually fetched for this specific run, not what got (or didn't get) written downstream.
        group.MapGet("/workflows/runs/{workflowRunId:guid}/launch-result", async (
            Guid workflowRunId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var payloads = await recorder.GetPagedAsync(workflowRunId, page: 1, pageSize: 50, cancellationToken);
            var sourcePayload = payloads.Items.FirstOrDefault(item => item.Contract == "ResourceBatch");
            if (sourcePayload is null)
            {
                return Results.Ok(new { patient = (JsonNode?)null });
            }

            var resources = (JsonNode.Parse(sourcePayload.PayloadJson) as JsonObject)?["Resources"] as JsonArray;
            var patientEntry = resources?.FirstOrDefault(
                resource => string.Equals(resource?["ResourceType"]?.GetValue<string>(), "Patient", StringComparison.OrdinalIgnoreCase));
            var patientJson = patientEntry?["Payload"]?.GetValue<string>();
            var patientResource = string.IsNullOrWhiteSpace(patientJson) ? null : JsonNode.Parse(patientJson);

            return Results.Ok(new
            {
                patient = patientResource,
                // The id a caller should pass as WorkflowRunRequest.PatientId on a later /run call against a
                // different workflow that shares this one's source connection, so it reuses this exact launch's
                // stored session/patient context instead of whichever session happens to be most recent by then.
                patientId = (patientResource as JsonObject)?["id"]?.GetValue<string>(),
            });
        });

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

        // Duplicates an existing workflow definition under a new name — every node/edge/config is copied exactly
        // (same node types, ranks, positions, and ConfigurationJson, so any source/destination/mapping ids embedded
        // in a node's config keep pointing at the same backing connections as the original). New Guids throughout
        // (workflow id + every node/edge id) via the same BuildWorkflow path every other create/save uses, so this
        // can never diverge from what a normal save would have produced.
        group.MapPost("/workflows/{workflowId:guid}/copy", async (
            Guid workflowId,
            CopyWorkflowRequest request,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var name = request.Name?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return Results.BadRequest(new { error = "invalid_request", error_description = "A name is required to copy a workflow." });
            }

            var source = await store.GetAsync(workflowId, cancellationToken);
            if (source is null)
            {
                return Results.NotFound();
            }

            var nodeRequests = source.Nodes
                .Select(node => new WorkflowNodeRequest(
                    node.Id.ToString(),
                    node.NodeType,
                    node.Category,
                    node.Rank,
                    node.SubRank,
                    node.DisplayName,
                    node.ConfigurationJson,
                    node.PositionX,
                    node.PositionY,
                    node.IsEnabled,
                    node.CheckpointUrlEnabled))
                .ToArray();

            var edgeRequests = source.Edges
                .Select(edge => new WorkflowEdgeRequest(edge.FromNodeId.ToString(), edge.ToNodeId.ToString()))
                .ToArray();

            var triggerRequest = source.Trigger is { } trigger
                ? new WorkflowTriggerRequest(trigger.Type, trigger.ScheduleExpression, trigger.IntervalMinutes, trigger.BackfillOnFirstRun)
                : null;

            // Always created disabled, regardless of the source's enabled state: an enabled Schedule/Poll trigger
            // firing immediately — in parallel with the original, against the same source/destination — would
            // double-run and double-write before the user has even reviewed the copy.
            var definitionRequest = new WorkflowDefinitionRequest(name, IsEnabled: false, nodeRequests, edgeRequests, triggerRequest);
            var copy = BuildWorkflow(Guid.NewGuid(), definitionRequest);
            await store.SaveAsync(copy, cancellationToken);

            return Results.Created($"/api/v1/workflows/{copy.Id}", copy);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

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
            ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var context = new WorkflowExecutionContext(
                Guid.NewGuid(),
                request?.CorrelationId ?? Guid.NewGuid().ToString("N"),
                triggeredBy: currentUserService.CurrentUser.AuditName,
                triggerType: "Manual",
                targetPatientId: request?.PatientId,
                patientSearchCriteria: request?.PatientSearchCriteria);
            var result = await orchestrator.ExecuteAsync(workflow, context, cancellationToken);

            return Results.Ok(result);
        });

        // Discards FHIRBridge's cached token for this workflow's source connection (both the given patientId's slot,
        // if any, and the unscoped "default" slot) — the next /run or launch requires a genuinely fresh interactive
        // sign-in. Does not call Epic/the authorization server itself; the token remains technically valid there
        // until it naturally expires, it is just no longer usable from FHIRBridge. Anonymous, matching /run's own
        // posture — a third-party app's own "Reset Token" action calls this directly.
        group.MapPost("/workflows/{workflowId:guid}/discard-token", async (
            Guid workflowId,
            string? patientId,
            IWorkflowDefinitionStore store,
            ISourceConnectionRuntimeResolver? sourceResolver,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            if (sourceResolver is null)
            {
                return Results.Ok(new { discarded = false });
            }

            Guid? sourceConnectionId = null;
            foreach (var node in workflow.Nodes.Where(n => n.Category == WorkflowNodeCategory.Source))
            {
                if (TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceId))
                {
                    sourceConnectionId = sourceId;
                    break;
                }
            }

            if (sourceConnectionId is null)
            {
                return Results.Ok(new { discarded = false });
            }

            await sourceResolver.DiscardTokenAsync(sourceConnectionId.Value, patientId, cancellationToken);
            return Results.Ok(new { discarded = true });
        });

        // Per-node checkpoint (docs/backend/05-workflow-node-checkpoints-plan.md §3.5). Admin-only: generates the
        // opaque URL for a node that already has CheckpointUrlEnabled set. Hitting the returned URL (anonymous,
        // below) runs only that node's ancestor closure and returns a run id.
        group.MapGet("/workflows/{workflowId:guid}/nodes/{nodeId:guid}/checkpoint-url", async (
            Guid workflowId,
            Guid nodeId,
            IWorkflowDefinitionStore store,
            ILaunchTokenProtector tokenProtector,
            HttpRequest httpRequest,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var node = workflow.Nodes.FirstOrDefault(n => n.Id == nodeId);
            if (node is null)
            {
                return Results.NotFound();
            }

            if (!node.CheckpointUrlEnabled)
            {
                return Results.BadRequest(new { error = "checkpoint_not_enabled", error_description = "Enable the checkpoint flag on this node before requesting its URL." });
            }

            var token = tokenProtector.ProtectWorkflowCheckpointContext(workflowId, nodeId);
            var url = $"{httpRequest.Scheme}://{httpRequest.Host}/api/v1/workflows/checkpoint/{token}";
            return Results.Ok(new { checkpointUrl = url });
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // The checkpoint URL itself. Anonymous — same trust model as /oauth/launch/{context}: the encrypted,
        // unguessable token is the boundary, not a session. Runs the target node's ancestor closure only and
        // returns a run id; fetch its output via /workflows/runs/{workflowRunId}/checkpoint-result.
        group.MapGet("/workflows/checkpoint/{token}", async (
            string token,
            ILaunchTokenProtector tokenProtector,
            IWorkflowDefinitionStore store,
            IRankedWorkflowOrchestrator orchestrator,
            CancellationToken cancellationToken) =>
        {
            var launchContext = tokenProtector.UnprotectContext(token);
            if (launchContext?.WorkflowId is not { } workflowId || launchContext.TargetNodeId is not { } targetNodeId)
            {
                return Results.BadRequest(new { error = "invalid_token", error_description = "This checkpoint URL is invalid or has expired." });
            }

            var workflow = await store.GetAsync(workflowId, cancellationToken);
            var node = workflow?.Nodes.FirstOrDefault(n => n.Id == targetNodeId);
            if (workflow is null || node is null || !node.CheckpointUrlEnabled)
            {
                return Results.NotFound(new { error = "checkpoint_unavailable", error_description = "This checkpoint no longer exists or has been disabled." });
            }

            var context = new WorkflowExecutionContext(
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                triggeredBy: "checkpoint-url",
                triggerType: "Checkpoint");
            var result = await orchestrator.ExecuteAsync(workflow, context, targetNodeId, cancellationToken);

            return Results.Ok(new { workflowRunId = result.WorkflowRun.Id });
        });

        // Companion to the checkpoint trigger above: returns the checkpointed node's captured output for a run,
        // resolved from just the run id (the frontend never needs to pass a node id around). Anonymous, same
        // trust model as /workflows/runs/{workflowRunId}/launch-result.
        group.MapGet("/workflows/runs/{workflowRunId:guid}/checkpoint-result", async (
            Guid workflowRunId,
            IWorkflowRunStore runStore,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var run = await runStore.GetAsync(workflowRunId, cancellationToken);
            if (run?.TargetNodeId is not { } targetNodeId)
            {
                return Results.NotFound(new { error = "not_a_checkpoint_run", error_description = "This run id is not a checkpoint run." });
            }

            var targetNodeRun = run.NodeRuns.FirstOrDefault(nodeRun => nodeRun.WorkflowNodeId == targetNodeId);
            if (targetNodeRun is null)
            {
                return Results.Ok(new { result = (JsonNode?)null, contract = (string?)null });
            }

            var payloads = await recorder.GetPagedAsync(workflowRunId, page: 1, pageSize: 50, cancellationToken);
            var payload = payloads.Items.FirstOrDefault(item => item.WorkflowNodeRunId == targetNodeRun.Id);

            return payload is null
                ? Results.Ok(new { result = (JsonNode?)null, contract = (string?)null })
                : Results.Ok(new { result = JsonNode.Parse(payload.PayloadJson), contract = payload.Contract });
        });

        // Persisted run history (Scenario A): node-by-node execution timeline for the builder UI.
        group.MapGet("/workflows/{workflowId:guid}/runs", async (
            Guid workflowId,
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
            Results.Ok(await runStore.ListByDefinitionAsync(workflowId, cancellationToken)));

        group.MapGet("/workflow-runs/{runId:guid}", async (
            Guid runId,
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            var run = await runStore.GetAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        // ── Execution History (global, across every workflow) ───────────────────────
        // Backs the portal's Execution History screen for the Runtime Plane — the path actually exercised by
        // "Run" and interactive (EHR launch / standalone) launches, as opposed to the Configured Pipeline's
        // separate route-execution history under /api/v1/pipeline-runs/route-executions.
        group.MapGet("/workflow-runs", async (
            string? status,
            string? source,
            string? triggeredBy,
            string? search,
            int? page,
            int? pageSize,
            IWorkflowRunStore runStore,
            IWorkflowDefinitionStore definitionStore,
            IConfigurationRepository configurationRepository,
            CancellationToken cancellationToken) =>
        {
            var runs = await runStore.ListRecentAsync(500, cancellationToken);
            var workflowsById = (await definitionStore.ListAsync(cancellationToken)).ToDictionary(w => w.Id);
            var sources = await configurationRepository.GetSourceConnectionsAsync(cancellationToken);
            var sourceInfoById = sources.ToDictionary(s => s.Id, s => (s.Name, SystemType: s.SourceSystemType.ToString()));

            var items = new List<WorkflowRunHistoryDto>();
            foreach (var run in runs)
            {
                if (!workflowsById.TryGetValue(run.WorkflowDefinitionId, out var workflow))
                {
                    continue;
                }

                var (sourceName, sourceSystemType) = ResolveWorkflowSource(workflow, sourceInfoById);

                items.Add(new WorkflowRunHistoryDto(
                    run.Id,
                    run.WorkflowDefinitionId,
                    workflow.Name,
                    sourceName,
                    sourceSystemType,
                    run.Status.ToString(),
                    run.StartedAt,
                    run.CompletedAt,
                    run.TriggeredBy,
                    run.TriggerType,
                    run.NodeRuns.Count,
                    run.ErrorMessage));
            }

            if (!string.IsNullOrWhiteSpace(status))
            {
                items = items.Where(x => string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                items = items.Where(x =>
                    string.Equals(x.SourceName, source, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.SourceSystemType, source, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(triggeredBy))
            {
                items = items.Where(x =>
                    string.Equals(x.TriggeredBy, triggeredBy, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.TriggerType, triggeredBy, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                items = items.Where(x =>
                    x.PipelineName.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (x.SourceName?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
            }

            var effectivePage = page is > 0 ? page.Value : 1;
            var effectivePageSize = pageSize is > 0 ? pageSize.Value : 25;
            var totalCount = items.Count;
            var paged = items
                .OrderByDescending(x => x.StartedAt)
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToList();

            return Results.Ok(new PagedResult<WorkflowRunHistoryDto>(paged, totalCount, effectivePage, effectivePageSize));
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        group.MapGet("/workflow-runs/{runId:guid}/summary", async (
            Guid runId,
            IWorkflowRunStore runStore,
            IWorkflowDefinitionStore definitionStore,
            IConfigurationRepository configurationRepository,
            CancellationToken cancellationToken) =>
        {
            var run = await runStore.GetAsync(runId, cancellationToken);
            if (run is null)
            {
                return Results.NotFound();
            }

            var workflow = await definitionStore.GetAsync(run.WorkflowDefinitionId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            var sources = await configurationRepository.GetSourceConnectionsAsync(cancellationToken);
            var sourceInfoById = sources.ToDictionary(s => s.Id, s => (s.Name, SystemType: s.SourceSystemType.ToString()));
            var (sourceName, sourceSystemType) = ResolveWorkflowSource(workflow, sourceInfoById);

            return Results.Ok(new WorkflowRunHistoryDto(
                run.Id,
                run.WorkflowDefinitionId,
                workflow.Name,
                sourceName,
                sourceSystemType,
                run.Status.ToString(),
                run.StartedAt,
                run.CompletedAt,
                run.TriggeredBy,
                run.TriggerType,
                run.NodeRuns.Count,
                run.ErrorMessage));
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Drill-down into what each node actually fetched/transformed/wrote. Returns decrypted PHI payloads, so
        // every call is itself audited — the same pattern used for the Configured Pipeline's equivalent endpoint.
        group.MapGet("/workflow-runs/{runId:guid}/resources", async (
            Guid runId,
            int? page,
            int? pageSize,
            IWorkflowNodeResourceHistoryRecorder recorder,
            IOperationalAuditService auditService,
            ICurrentUserService currentUserService,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetPagedAsync(
                runId,
                page is > 0 ? page.Value : 1,
                pageSize is > 0 ? pageSize.Value : 25,
                cancellationToken);

            await auditService.RecordAsync(
                new RecordOperationalAuditLogRequest(
                    null, null, null, null, null, null,
                    "ExecutionDetailViewed",
                    "Completed",
                    $"Execution history detail viewed for workflow run {runId}.",
                    result.Items.Count,
                    currentUserService.CurrentUser.AuditName,
                    null),
                cancellationToken);

            return Results.Ok(result);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

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

        // Opts a workflow into (or out of) the anonymous public-standalone-url mint endpoint (see OAuthController.
        // GetPublicWorkflowStandaloneUrl) — required before that endpoint will mint a launch context for it.
        // Explicitly admin-gated: this is the only thing standing between "any caller who knows this workflowId"
        // and a working Epic-login link for it.
        group.MapPost("/workflows/{workflowId:guid}/enable-public-launch", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            workflow.EnablePublicLaunch();
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Ok(workflow);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        group.MapPost("/workflows/{workflowId:guid}/disable-public-launch", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            workflow.DisablePublicLaunch();
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Ok(workflow);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Permanently delete a workflow definition (and its nodes/edges/config). The referenced source/destination/
        // mapping records are NOT deleted — they may be shared with other workflows/routes. Admin-only.
        group.MapDelete("/workflows/{workflowId:guid}", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            await store.DeleteAsync(workflowId, cancellationToken);
            return Results.NoContent();
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        return endpoints;
    }

    private static WorkflowNodeRequest WithConfiguration(WorkflowNodeRequest node, Action<JsonObject> mutate)
    {
        var config = TryParseConfiguration(node.ConfigurationJson) ?? new JsonObject();
        mutate(config);
        return node with { ConfigurationJson = config.ToJsonString() };
    }

    private static bool TryResolveEntityId(
        string nodeId,
        IReadOnlyDictionary<string, Guid> createdIds,
        IReadOnlyDictionary<string, WorkflowNodeRequest> nodes,
        string configurationKey,
        out Guid entityId)
    {
        if (createdIds.TryGetValue(nodeId, out entityId))
        {
            return true;
        }

        // Picker flow: the referenced node already carries the entity id in its configuration.
        if (nodes.TryGetValue(nodeId, out var node)
            && TryParseConfiguration(node.ConfigurationJson) is { } config
            && config[configurationKey]?.ToString() is { } raw
            && Guid.TryParse(raw, out entityId))
        {
            return true;
        }

        entityId = Guid.Empty;
        return false;
    }

    // First Source-category node whose referenced connection resolves — good enough for Execution History display
    // (unlike /workflows/summary, this doesn't need the launch-vs-run precedence rule, just a name to show).
    private static (string? Name, string? SystemType) ResolveWorkflowSource(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<Guid, (string Name, string SystemType)> sourceInfoById)
    {
        foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Source))
        {
            if (TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceId)
                && sourceInfoById.TryGetValue(sourceId, out var info))
            {
                return (info.Name, info.SystemType);
            }
        }

        return (null, null);
    }

    // Resolves a workflow's destination node → its created destination id + target table name, falling back to the
    // mapping profile targeting that destination when the node itself doesn't carry the table name (the writer
    // derives it from the bound mapping at write time). Shared by destination-data and launch-result.
    private static async Task<(Guid DestinationId, string DestinationObject)> ResolveDestinationTargetAsync(
        WorkflowDefinition workflow,
        IConfigurationRepository configurationRepository,
        CancellationToken cancellationToken)
    {
        var destinationId = Guid.Empty;
        var destinationObject = string.Empty;
        foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Destination))
        {
            if (TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out destinationId))
            {
                destinationObject = GetConfigurationString(node.ConfigurationJson, "destinationObject")
                    ?? GetConfigurationString(node.ConfigurationJson, "target")
                    ?? string.Empty;
                break;
            }
        }

        if (destinationId == Guid.Empty || !string.IsNullOrWhiteSpace(destinationObject))
        {
            return (destinationId, destinationObject);
        }

        var mappings = await configurationRepository.GetMappingProfilesAsync(cancellationToken);
        destinationObject = mappings
            .Where(mapping => mapping.DestinationId == destinationId
                && !string.IsNullOrWhiteSpace(mapping.DestinationObject))
            .Select(mapping => mapping.DestinationObject)
            .FirstOrDefault() ?? string.Empty;

        return (destinationId, destinationObject);
    }

    private static bool TryGetConfigurationGuid(string? configurationJson, string key, out Guid value)
    {
        value = Guid.Empty;
        return TryParseConfiguration(configurationJson) is { } config
            && config[key]?.ToString() is { } raw
            && Guid.TryParse(raw, out value);
    }

    private static string? GetConfigurationString(string? configurationJson, string key)
        => TryParseConfiguration(configurationJson) is { } config ? config[key]?.ToString() : null;

    private static JsonObject? TryParseConfiguration(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(configurationJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static WorkflowDefinition BuildWorkflow(Guid workflowId, WorkflowDefinitionRequest request)
    {
        var workflow = new WorkflowDefinition(workflowId, request.Name, version: 1, request.IsEnabled, request.IsPubliclyLaunchable);
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
                nodeRequest.IsEnabled,
                nodeRequest.CheckpointUrlEnabled);

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

        workflow.SetTrigger(MapTrigger(request.Trigger));
        return workflow;
    }

    // Validates + maps the optional trigger. Manual (or absent) → null (run on demand). Schedule requires a cron with
    // 5 or 6 fields; Poll requires a positive interval. Throws (→ 400) on malformed input.
    private static WorkflowTrigger? MapTrigger(WorkflowTriggerRequest? request)
    {
        if (request is null || request.Type == WorkflowTriggerType.Manual)
        {
            return null;
        }

        if (request.Type == WorkflowTriggerType.Schedule)
        {
            var fieldCount = (request.ScheduleExpression ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            if (fieldCount is not (5 or 6))
            {
                throw new InvalidOperationException("A Schedule trigger requires a 5- or 6-field cron expression.");
            }
        }

        if (request.Type == WorkflowTriggerType.Poll && request.IntervalMinutes is not > 0)
        {
            throw new InvalidOperationException("A Poll trigger requires a positive interval in minutes.");
        }

        return new WorkflowTrigger(
            request.Type,
            request.ScheduleExpression,
            request.IntervalMinutes,
            request.BackfillOnFirstRun);
    }
}
