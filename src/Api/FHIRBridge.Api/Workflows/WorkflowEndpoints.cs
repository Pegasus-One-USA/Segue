using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;

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
            IConfigurationRepository configurationRepository,
            IWorkflowDefinitionStore store,
            IEpicSourceConnectionScopeSyncService scopeSyncService,
            IParentReferenceResolver parentReferenceResolver,
            IDestinationSchemaService destinationSchemaService,
            CancellationToken cancellationToken) =>
        {
            // Fail fast, before provisioning anything: every "child of" declaration on a mapping spec must
            // resolve to a real reference field, mapped, targeting a sibling resource on the same destination.
            var parentReferenceError = ValidateMappingParentReferences(request.Mappings ?? [], parentReferenceResolver);
            if (parentReferenceError is not null)
            {
                return Results.BadRequest(parentReferenceError);
            }

            // Destinations, Sources, Mappings, and the workflow-definition save below used to each commit
            // independently — a validation failure or exception partway through (e.g. a mapping's column not
            // existing on the real table) left everything created so far durably persisted with no way to retry
            // cleanly: resubmitting the same request tries to create the same source connection again and hits a
            // name-uniqueness violation, since the first attempt's row never went away. Wrapping the whole
            // sequence in one transaction makes it all-or-nothing: only CommitAsync (right before the success
            // return) makes any of it durable — every early `return` below leaves this undisposed-without-commit,
            // which rolls the transaction back.
            await using var transaction = await configurationRepository.BeginTransactionAsync(cancellationToken);

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
            // A destination selecting more than one resource (Patient + Observation + Condition, say) produces one
            // spec per resource, all sharing the SAME mapping node id — the canvas has one "Field Mapping" node
            // whose wizard-authored config already carries every selected resource's field rows; what's created
            // here is one MappingProfile row per resource. Every spec for a given node id must accumulate into that
            // node's config rather than overwrite it, or only the last-processed resource would survive on the node
            // (MappingNodeExecutor resolves its fields from whichever mappingProfileId(s) end up there).
            var profileIdsByNode = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            // Same accumulate-don't-overwrite need applies on the DESTINATION side: a destination node fed by
            // several specs (Patient + Condition + Observation sharing one SQL Server destination) must remember
            // every resource's own target table + fields, not just the first one saved — see the destination
            // mirroring block below and DestinationNodeExecutor.CreateMappingProfiles.
            var resourceMappingsByNode = new Dictionary<string, Dictionary<string, DestinationResourceMappingConfig>>(StringComparer.OrdinalIgnoreCase);
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

                // Defense in depth against the wizard silently persisting a column that doesn't exist on the
                // customer's own table (see the "Invalid column name" incidents this guards against — a
                // destination table with no PK/unique constraint leaves the id row's column an unverified,
                // editable guess client-side). Best-effort: only blocks the save when the live schema was
                // actually reachable and the target table's real columns are known: an unreachable/non-relational
                // destination can't be validated this way and is left to the frontend-side check instead, not
                // hard-failed here.
                var columnError = await ValidateMappedColumnsExistAsync(
                    spec, destinationId, destinationSchemaService, cancellationToken);
                if (columnError is not null)
                {
                    return Results.BadRequest(columnError);
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

                if (!profileIdsByNode.TryGetValue(spec.NodeId, out var idsForNode))
                {
                    idsForNode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    profileIdsByNode[spec.NodeId] = idsForNode;
                }
                idsForNode[spec.ResourceType] = mapping.Id.ToString();

                nodes[spec.NodeId] = WithConfiguration(node, config =>
                {
                    // Kept for backward compatibility with anything still reading the single legacy field (reflects
                    // whichever resource was processed last when there's more than one — MappingNodeExecutor prefers
                    // mappingProfileIds below whenever it's present, so this is display/compat-only in that case).
                    config["mappingProfileId"] = mapping.Id.ToString();
                    config["mappingProfileIds"] = JsonSerializer.SerializeToNode(idsForNode, WebJsonOptions);
                });

                // The destination executor rebuilds its write-time mapping (target table + the columns it auto-creates)
                // from its OWN node config rather than resolving the mapping by id, so mirror every spec's target and
                // fields onto the destination node under resourceMappings, keyed by resource type — the same
                // accumulate-don't-overwrite treatment as profileIdsByNode above, and for the same reason: a
                // destination fed by more than one resource spec (Patient + Condition + Observation sharing one SQL
                // Server destination, say) must let DestinationNodeExecutor route each resource type's records to its
                // own table/columns instead of forcing every resource type through whichever one saved first (that
                // used to silently misroute every resource but the first into the wrong table, failing with
                // "Invalid column name"). The single legacy resourceType/destinationObject/fields trio is still
                // mirrored from the first spec only, kept only for any older consumer still reading that single shape.
                if (nodes.TryGetValue(spec.DestinationNodeId, out var destinationNode))
                {
                    if (!resourceMappingsByNode.TryGetValue(spec.DestinationNodeId, out var resourceMappingsForNode))
                    {
                        resourceMappingsForNode = new Dictionary<string, DestinationResourceMappingConfig>(StringComparer.OrdinalIgnoreCase);
                        resourceMappingsByNode[spec.DestinationNodeId] = resourceMappingsForNode;
                    }
                    resourceMappingsForNode[spec.ResourceType] = new DestinationResourceMappingConfig(spec.DestinationObject, spec.Fields);

                    var mirrorLegacyShape = ReadConfigString(destinationNode, "resourceType") is null;
                    nodes[spec.DestinationNodeId] = WithConfiguration(destinationNode, config =>
                    {
                        if (mirrorLegacyShape)
                        {
                            config["resourceType"] = spec.ResourceType;
                            config["destinationObject"] = spec.DestinationObject;
                            config["fields"] = JsonSerializer.SerializeToNode(spec.Fields, WebJsonOptions);
                        }

                        config["resourceMappings"] = JsonSerializer.SerializeToNode(resourceMappingsForNode, WebJsonOptions);
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

            // A WorkflowId on the request means this build is re-saving an existing workflow, not creating a new
            // one — bump the version off whatever is currently stored so version history is real instead of always 1.
            var existingDefinition = request.WorkflowId is { } existingWorkflowId
                ? await store.GetAsync(existingWorkflowId, cancellationToken)
                : null;
            var workflow = BuildWorkflow(
                request.WorkflowId ?? Guid.NewGuid(), definitionRequest, (existingDefinition?.Version ?? 0) + 1);
            await store.SaveAsync(workflow, cancellationToken);

            // Everything above (destinations, sources, mappings, the workflow definition itself) is durable only
            // from this point on — nothing before here survives if any step failed or threw.
            await transaction.CommitAsync(cancellationToken);

            // Re-derive each referenced source connection's OAuth scopes from what every pipeline sharing it
            // actually consumes downstream, now that this save may have changed a destination's resource selection
            // (or introduced/removed a workflow referencing the connection). Distinct: the same connection can be
            // wired to more than one source node spec in a single build request.
            var syncedScopes = new Dictionary<Guid, IReadOnlyList<string>>();
            foreach (var sourceConnectionId in sourceIds.Values.Distinct())
            {
                var scopes = await scopeSyncService.SyncAsync(sourceConnectionId, cancellationToken);
                if (scopes is not null)
                {
                    syncedScopes[sourceConnectionId] = scopes;
                }
            }

            var result = new WorkflowBuildResult(workflow.Id, sourceIds, destinationIds, mappingIds, syncedScopes);
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

        // Source Connections page: which source-connection ids are referenced by at least one workflow's Source
        // node right now — used to block Edit/Delete on a connection a workflow still depends on. Deliberately
        // returns every referencing connection across every workflow (not just one per workflow, unlike
        // /workflows/summary's launch-precedence resolvedSourceId), since a workflow can have more than one
        // Source node and any of them being wired to this connection should still count as "in use."
        group.MapGet("/workflows/source-connection-usage", async (
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflows = await store.ListAsync(cancellationToken);

            var usedSourceConnectionIds = workflows
                .SelectMany(workflow => workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Source))
                .Select(node => TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceId)
                    ? sourceId
                    : (Guid?)null)
                .Where(sourceId => sourceId is not null)
                .Select(sourceId => sourceId!.Value)
                .Distinct()
                .ToArray();

            return Results.Ok(usedSourceConnectionIds);
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Destination Connections page: which destination ids are referenced by at least one workflow's Destination
        // node right now — used to block Delete on a connection a workflow still depends on, independent of whether
        // that workflow has ever actually run (see HasDestinationExecutionHistoryAsync for the run-history gate,
        // which is a different signal). Mirrors source-connection-usage above exactly, one category over.
        group.MapGet("/workflows/destination-usage", async (
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            var workflows = await store.ListAsync(cancellationToken);

            var usedDestinationIds = workflows
                .SelectMany(workflow => workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Destination))
                .Select(node => TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out var destinationId)
                    ? destinationId
                    : (Guid?)null)
                .Where(destinationId => destinationId is not null)
                .Select(destinationId => destinationId!.Value)
                .Distinct()
                .ToArray();

            return Results.Ok(usedDestinationIds);
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

            // Scope the preview to this workflow's own runs (each run id is the PipelineRunId stamped on the rows it
            // wrote), so a shared destination table shows only what THIS workflow produced — and nothing for a
            // workflow that hasn't run yet.
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
            var existing = await store.GetAsync(workflowId, cancellationToken);
            var workflow = BuildWorkflow(workflowId, request, (existing?.Version ?? 0) + 1);
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
            IGlobalExceptionManager exceptionManager,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            // Reuse the ambient request's correlation id (the same one ApiRequestLoggingHandler already stamps on
            // every outbound HTTP call this run triggers) rather than minting an unrelated one — otherwise an
            // ErrorLogs row from this run can never be found via its own outbound API Requests, and vice versa.
            var context = new WorkflowExecutionContext(
                Guid.NewGuid(),
                request?.CorrelationId ?? currentUserService.CurrentUser.CorrelationId ?? Guid.NewGuid().ToString("N"),
                triggeredBy: currentUserService.CurrentUser.AuditName,
                triggerType: "Manual",
                targetPatientId: request?.PatientId,
                patientSearchCriteria: request?.PatientSearchCriteria,
                callerId: request?.CallerId);

            try
            {
                var result = await orchestrator.ExecuteAsync(workflow, context, cancellationToken);
                return Results.Ok(result);
            }
            catch (FHIRBridgeException businessRule)
            {
                // Expected business-rule rejection — return the safe UserMessage inline; NOT a technical error.
                return Results.Json(
                    new { error = businessRule.UserMessage, message = businessRule.UserMessage },
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Unexpected/technical failure (e.g. an external system returned an error page). Capture the FULL
                // technical detail to ErrorLogs with a reference id, and return ONLY a generic, PHI-safe message +
                // that reference — never the raw exception text (which can contain an entire HTML error page).
                var report = await exceptionManager.CaptureAsync(
                    exception,
                    new ExceptionContext(
                        Module: "Runtime",
                        Severity: "Error",
                        CorrelationId: context.CorrelationId,
                        ExecutionId: context.WorkflowRunId.ToString(),
                        WorkflowId: workflowId.ToString()),
                    cancellationToken);

                return Results.Json(
                    new
                    {
                        error = report.UserFriendlyMessage,
                        message = report.UserFriendlyMessage,
                        errorReferenceId = report.ErrorReferenceId,
                        correlationId = report.CorrelationId,
                        category = report.Category.ToString(),
                    },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });

        // Discards FHIRBridge's cached token for this workflow's source connection (both the given patientId's slot,
        // if any, and the unscoped "default" slot) — the next /run or launch requires a genuinely fresh interactive
        // sign-in. Does not call Epic/the authorization server itself; the token remains technically valid there
        // until it naturally expires, it is just no longer usable from FHIRBridge. Anonymous, matching /run's own
        // posture — a third-party app's own "Reset Token" action calls this directly.
        group.MapPost("/workflows/{workflowId:guid}/discard-token", async (
            Guid workflowId,
            string? patientId,
            string? callerId,
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

            await sourceResolver.DiscardTokenAsync(sourceConnectionId.Value, patientId, cancellationToken, callerId);
            return Results.Ok(new { discarded = true });
        });

        // Cheaply reports whether a real /run against this workflow's source connection would currently succeed
        // authentication-wise, WITHOUT running any pipeline — no WorkflowRun row, no orchestrator, no Epic API call
        // in the common case (see HasValidTokenAsync). Lets a caller decide "redirect to interactive sign-in" vs.
        // "just fetch" up front, instead of learning it only from a failed /run attempt. Anonymous, matching
        // /run and /discard-token's own posture.
        group.MapGet("/workflows/{workflowId:guid}/token-status", async (
            Guid workflowId,
            string? patientId,
            string? callerId,
            IWorkflowDefinitionStore store,
            ISourceConnectionRuntimeResolver? sourceResolver,
            ILogger<Program> logger,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            if (sourceResolver is null)
            {
                return Results.Ok(new { hasValidToken = false });
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
                return Results.Ok(new { hasValidToken = false });
            }

            var hasValidToken = await sourceResolver.HasValidTokenAsync(sourceConnectionId.Value, patientId, cancellationToken, callerId);
            logger.LogInformation(
                "token-status check: workflowId={WorkflowId} sourceConnectionId={SourceConnectionId} " +
                "hasCallerId={HasCallerId} hasPatientId={HasPatientId} hasValidToken={HasValidToken}",
                workflowId, sourceConnectionId, !string.IsNullOrWhiteSpace(callerId), !string.IsNullOrWhiteSpace(patientId),
                hasValidToken);
            return Results.Ok(new { hasValidToken });
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
            ICurrentUserService currentUserService,
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

            // See the /run endpoint's matching comment: reuse the ambient correlation id so this run's ErrorLogs
            // (if any) can be found via the same id as its outbound API Requests.
            var context = new WorkflowExecutionContext(
                Guid.NewGuid(),
                currentUserService.CurrentUser.CorrelationId ?? Guid.NewGuid().ToString("N"),
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
                    run.ErrorMessage,
                    run.WorkflowDefinitionVersion));
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
                run.ErrorMessage,
                run.WorkflowDefinitionVersion));
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Drill-down into what each node actually fetched/transformed/wrote. Returns decrypted PHI payloads.
        group.MapGet("/workflow-runs/{runId:guid}/resources", async (
            Guid runId,
            int? page,
            int? pageSize,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetPagedAsync(
                runId,
                page is > 0 ? page.Value : 1,
                pageSize is > 0 ? pageSize.Value : 25,
                cancellationToken);

            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

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

    // Sums the "count" metadata every Source-category node executor reports (see SourceNodeExecutor), and flags
    // whether any of them actually ran via bulk export ($export), so the /run endpoint's Activity Feed entry can
    // say "Bulk Export completed — N imported" instead of the generic wording, without duplicating the executor's
    // own bulk-vs-search resolution logic. A fan-out workflow with a mix of retrieval methods is still labeled as
    // a Bulk Export — that's the more notable event to surface even when it's not the only source node.
    private static (int ImportedCount, bool UsedBulkExport) SummarizeSourceNodeResults(
        WorkflowDefinition workflow, WorkflowRunResult result)
    {
        var total = 0;
        var usedBulkExport = false;
        foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Source))
        {
            if (!result.OutputsByNodeId.TryGetValue(node.Id, out var output))
            {
                continue;
            }

            if (output.Metadata.TryGetValue("count", out var rawCount) && rawCount is int count)
            {
                total += count;
            }

            if (output.Metadata.TryGetValue("retrievalMethod", out var rawMethod)
                && rawMethod is string method
                && string.Equals(method, "bulk-export", StringComparison.OrdinalIgnoreCase))
            {
                usedBulkExport = true;
            }
        }

        return (total, usedBulkExport);
    }

    // Validates every ParentReferenceSpec across the build request in one pass, before anything is created.
    // "Same destination" (grouping by DestinationNodeId) is this flow's stand-in for "same route" — the canvas
    // has no persisted ResourcePipelineRoute to group by, but every resource a destination-wizard session
    // selects together shares the same destination node, which is the equivalent scope for "resources
    // configured together." Returns the first validation failure found, or null if everything resolves.
    private static string? ValidateMappingParentReferences(
        IReadOnlyCollection<MappingBuildSpec> mappings,
        IParentReferenceResolver parentReferenceResolver)
    {
        foreach (var group in mappings.GroupBy(m => m.DestinationNodeId, StringComparer.OrdinalIgnoreCase))
        {
            var byResourceType = group
                .GroupBy(m => m.ResourceType, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            foreach (var spec in group)
            {
                foreach (var link in spec.ParentReferences ?? [])
                {
                    if (!byResourceType.ContainsKey(link.ParentResourceType))
                    {
                        return $"'{spec.ResourceType}' is configured as a child of '{link.ParentResourceType}', " +
                            "which is not one of this destination's selected resources.";
                    }

                    var requiredField = parentReferenceResolver.Resolve(
                        spec.ResourceType, link.ParentResourceType, link.ReferenceFieldOverride);

                    if (requiredField is null)
                    {
                        return $"'{spec.ResourceType}' has no FHIR reference field that can target " +
                            $"'{link.ParentResourceType}'.";
                    }

                    // MappingFieldDto (this create-request shape) has no IsEnabled flag — a disabled row in the
                    // wizard is simply never included in the built request, so presence in Fields already means
                    // "enabled" here (unlike MappingField, the persisted domain record ConfigurationService
                    // validates separately for the ResourcePipelineRoute path).
                    var isMapped = spec.Fields.Any(f =>
                        string.Equals(f.JsonPath, requiredField.JsonPath, StringComparison.Ordinal));

                    if (!isMapped)
                    {
                        return $"'{spec.ResourceType}' must map '{requiredField.FhirPath}' because it is " +
                            $"configured as a child of '{link.ParentResourceType}'.";
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Best-effort check that every mapped field's <see cref="MappingFieldDto.TargetField"/> is a real column on
    /// the destination's live table — the id/upsert-key field above all, since an unverified guess there is what
    /// produces a run-time "Invalid column name" against a customer-owned table with no PK/unique constraint to
    /// auto-detect from. Only validates when the destination is relational and its schema was actually reachable
    /// (<see cref="IDestinationSchemaService.GetSchemaAsync"/> returns an empty table list for non-relational
    /// destinations, and this method treats a lookup failure — deleted destination, transient connectivity — as
    /// "can't verify" rather than a hard failure): the frontend-side check in the wizard is the first line of
    /// defense for those cases, this is defense in depth for whatever reaches this endpoint regardless of how.
    /// </summary>
    private static async Task<string?> ValidateMappedColumnsExistAsync(
        MappingBuildSpec spec,
        Guid destinationId,
        IDestinationSchemaService destinationSchemaService,
        CancellationToken cancellationToken)
    {
        DestinationSchemaDto schema;
        try
        {
            schema = await destinationSchemaService.GetSchemaAsync(destinationId, cancellationToken);
        }
        catch
        {
            return null;
        }

        if (schema.Tables.Count == 0)
        {
            return null;
        }

        var tableIdentifier = spec.DestinationObject.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [var name, ..]
            ? name
            : spec.DestinationObject;

        var table = schema.Tables.FirstOrDefault(t =>
            string.Equals(t.FullName, tableIdentifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.TableName, tableIdentifier, StringComparison.OrdinalIgnoreCase));

        // The configured table isn't one this destination's live schema actually has — a different, more specific
        // failure than a bad column, and one the writer already reports clearly at run time; nothing further to
        // check here since there are no real columns to validate field names against.
        if (table is null)
        {
            return null;
        }

        var realColumns = new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var field in spec.Fields)
        {
            if (!realColumns.Contains(field.TargetField))
            {
                return field.IsUpsertKey
                    ? $"'{spec.ResourceType}': the id/upsert-key field is mapped to column '{field.TargetField}', " +
                        $"which does not exist on '{table.FullName}'. Pick a real column from that table."
                    : $"'{spec.ResourceType}': field '{field.TargetField}' does not exist on '{table.FullName}'. " +
                        "Pick a real column from that table.";
            }
        }

        return null;
    }

    private static WorkflowNodeRequest WithConfiguration(WorkflowNodeRequest node, Action<JsonObject> mutate)
    {
        var config = TryParseConfiguration(node.ConfigurationJson) ?? new JsonObject();
        mutate(config);
        return node with { ConfigurationJson = config.ToJsonString() };
    }

    private static string? ReadConfigString(WorkflowNodeRequest node, string key) =>
        TryParseConfiguration(node.ConfigurationJson)?[key]?.ToString();

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

    private static WorkflowDefinition BuildWorkflow(Guid workflowId, WorkflowDefinitionRequest request, int version = 1)
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
