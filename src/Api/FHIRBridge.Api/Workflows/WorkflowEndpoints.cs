using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Api.Security;
using FHIRBridge.Api.Workflows;
using FHIRBridge.Application.Abstractions.Caching;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Licensing;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Abstractions.Workflows;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Mappings;
using FHIRBridge.Application.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.Runtime.Application.Abstractions.Applications;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Application.Workflows.Validation;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.Governance;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Api.Workflows;

public static class WorkflowEndpoints
{
    // Node executors read config with JsonSerializerDefaults.Web (camelCase); serialize embedded fields the same way.
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

    // The nodes that sit between a source and its destination and resolve transformation rules for themselves —
    // mirrors the portal's own CHAIN_NODE_TYPES (workflow-graph-mapper-v2.service.ts). String literals for the
    // same reason the mappingNodeType constant below is one: WorkflowNodeTypes lives in Runtime.Application,
    // which this file does not reference.
    private static readonly string[] ChainNodeTypes =
        ["MappingNode", "FhirResourceTransformNode", "DeIdentificationNode"];

    // Standardizes every /workflows/build validation rejection to the same { error, message, fieldErrors } shape
    // Program.cs's MapException already produces for RequestValidationException, instead of the bare strings this
    // endpoint used to return — so the Angular error handler has one shape to read regardless of which check failed.
    private static IResult ValidationBadRequest(string message) =>
        Results.BadRequest(new { error = message, message, fieldErrors = (IReadOnlyDictionary<string, string[]>?)null });

    private static IResult ValidationBadRequest(string field, string message) =>
        Results.BadRequest(new
        {
            error = message,
            message,
            fieldErrors = (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]> { [field] = [message] },
        });

    public static IEndpointRouteBuilder MapWorkflowEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints
            .MapGroup("/api/v1")
            .WithTags("Workflows");

        group.MapGet("/workflow-catalog", (IWorkflowNodeCatalog catalog) => Results.Ok(catalog.List()));

        // Low-level create: persists a raw WorkflowDefinitionRequest graph with no source/destination/mapping
        // provisioning (that's /workflows/build below, which the portal's builder canvas actually calls) —
        // always mints a new workflow id, so this is pure create semantics.
        group.MapPost("/workflows", async (
            WorkflowDefinitionRequest request,
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
        {
            // License workflow-quota enforcement lives centrally in LicenseEnforcementSaveChangesInterceptor,
            // which distinguishes this genuine create from an edit at the actual persistence choke point
            // (SqlWorkflowDefinitionStore.SaveAsync) rather than here.
            var workflow = BuildWorkflow(Guid.NewGuid(), request);
            await store.SaveAsync(workflow, cancellationToken);
            return Results.Created($"/api/v1/workflows/{workflow.Id}", workflow);
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Create)));

        // Option B create-on-save: provision secrets + create Source/Destination/Mapping records, inject their ids into
        // the referencing nodes, then persist the graph. One call turns a builder canvas into a launchable workflow that
        // resolves real, RBAC-scoped configuration by id at run time. Gated to admins because it provisions secrets.
        group.MapPost("/workflows/build", async (
            WorkflowBuildRequest request,
            IConfigurationService configurationService,
            IConfigurationRepository configurationRepository,
            IWorkflowDefinitionStore store,
            IWorkflowConfigurationCleanupService workflowConfigurationCleanup,
            IEpicSourceConnectionScopeSyncService scopeSyncService,
            IParentReferenceResolver parentReferenceResolver,
            IDestinationSchemaService destinationSchemaService,
            IAuthorizationService authorizationService,
            IServiceProvider serviceProvider,
            [FromKeyedServices(FhirElementCatalogKeys.Generic)] IFhirElementCatalog genericFhirCatalog,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            // A WorkflowId on the request means this build is re-saving an EXISTING workflow (same branch
            // BuildWorkflow's version-bump logic below tests) — that's an edit of something that already
            // exists, not the creation of a new one, so it needs workflow.edit rather than workflow.create.
            // Route-level .RequireAuthorization() below only asserts "authenticated"; the actual
            // create-vs-edit permission can only be resolved here, once the body is bound.
            var requiredWorkflowAction = request.WorkflowId is null ? PermissionActionCode.Create : PermissionActionCode.Edit;
            if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                    authorizationService, httpContext.User, PermissionGroupCode.Workflow, requiredWorkflowAction))
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // License workflow-quota enforcement (create vs. edit — an edit never changes row count) lives
            // centrally in LicenseEnforcementSaveChangesInterceptor, resolved at the actual persistence choke
            // point (SqlWorkflowDefinitionStore.SaveAsync) rather than here.

            // Re-derive any mapping field whose JsonPath is missing its array wildcards, BEFORE anything is
            // validated or persisted, so both the mapping node's inline "mappings" block and the destination
            // node's "resourceMappings" (built from the same spec.Fields below) get the corrected path.
            //
            // The wizard stamps the catalog's array-aware JsonPath ("$.name[*].family") onto each row, but the
            // catalog is fetched asynchronously: a field mapped before it arrives falls back to the built-in
            // DEST_RESOURCE_DEFS, which carry no JsonPath at all, and the client then builds a naive
            // "$.name.family". That resolves to nothing against an array (JsonMappingEngine.ResolveAll needs an
            // object to read a property from), so every array-nested column — name, address, identifier —
            // silently writes NULL, and any transformation rule on that column never runs because there is no
            // value to transform. It only looked correct on a second edit, when the catalog was already cached.
            //
            // The server always has the catalog, so it is the right place to repair this. Deliberately reuses
            // the catalog's own pre-computed JsonPath rather than appending "[*]" to every ancestor segment:
            // only genuinely repeating elements are wildcarded (Condition.code.coding.code wildcards "coding"
            // but not "code") — see MappingImportService.BuildResolvableJsonPath, which documents the
            // production bug that naive approach caused.
            request = request with
            {
                Mappings = await RepairMappingJsonPathsAsync(
                    request, configurationRepository, serviceProvider, genericFhirCatalog, cancellationToken),
            };

            // Fail fast, before provisioning anything: every "child of" declaration on a mapping spec must
            // resolve to a real reference field, mapped, targeting a sibling resource on the same destination.
            var parentReferenceError = ValidateMappingParentReferences(request.Mappings ?? [], parentReferenceResolver);
            if (parentReferenceError is not null)
            {
                return ValidationBadRequest(parentReferenceError);
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
            // Every source connection this build actually references, including ones resolved via the "Existing
            // Source" picker fallback in TryResolveEntityId below (never added to sourceIds itself — that dictionary
            // only ever holds freshly created/updated connections from the request.Sources loop). Scope/retrieval
            // sync below must run for BOTH, or a shared connection reused unchanged via "Existing Source" never
            // gets its Retrieval.ResourceTypes kept in sync with what its destinations actually consume — the whole
            // point of allowing "Existing Source" to be usable for athenahealth at all.
            var allReferencedSourceConnectionIds = new HashSet<Guid>();

            // A WorkflowId means this build is re-saving an existing workflow — loaded here (rather than only
            // where the version bump reads it further down) so a removed node can be checked before any of this
            // request's writes happen; the version-bump usage below reuses this same instance.
            var existingDefinition = request.WorkflowId is { } existingWorkflowIdForRemovalCheck
                ? await store.GetAsync(existingWorkflowIdForRemovalCheck, cancellationToken)
                : null;

            // Removing a node from a workflow requires BOTH workflow.edit (already checked above) AND that
            // node's own vendor `.delete` permission (epic.delete, sqlserver.delete, ...) — same per-vendor
            // pattern Create/Edit already use above, so a role can be blocked from removing one specific
            // vendor's node even though it can otherwise edit this workflow. "Removed" is detected via the
            // underlying SourceConnection/DestinationConfiguration id every node carries in its own config,
            // not via matching node ids — node identity isn't stable across saves (every save recreates every
            // WorkflowNode row with a fresh id; see WorkflowDefinition.AddNode), so the connection/destination
            // id is the only thing that reliably survives from one save to the next. Deliberately does NOT
            // touch sourceconnections.delete/destinationconnections.delete (the codes SourceConnectionsController
            // .Delete/ConfigurationsController.DeleteDestinationConfiguration check for deleting the underlying
            // stored connection in Settings) — that remains a fully separate action from removing a node here.
            if (existingDefinition is not null)
            {
                var newSourceConnectionIds = CollectConfigurationGuids(request.Nodes.Select(n => n.ConfigurationJson), "sourceConnectionId");
                var removedSourceConnectionIds = CollectConfigurationGuids(existingDefinition.Nodes.Select(n => n.ConfigurationJson), "sourceConnectionId")
                    .Except(newSourceConnectionIds);
                foreach (var removedSourceConnectionId in removedSourceConnectionIds)
                {
                    var removedSource = await configurationService.GetSourceConnectionByIdAsync(removedSourceConnectionId, cancellationToken);
                    if (removedSource is null) continue; // already gone (e.g. deleted elsewhere) — nothing left to protect
                    if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                            authorizationService, httpContext.User, removedSource.SourceSystemType, PermissionActionCode.Delete))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }

                var newDestinationIds = CollectConfigurationGuids(request.Nodes.Select(n => n.ConfigurationJson), "destinationId");
                var removedDestinationIds = CollectConfigurationGuids(existingDefinition.Nodes.Select(n => n.ConfigurationJson), "destinationId")
                    .Except(newDestinationIds);
                foreach (var removedDestinationId in removedDestinationIds)
                {
                    var removedDestination = await configurationRepository.GetDestinationAsync(removedDestinationId, cancellationToken);
                    if (removedDestination is null) continue;
                    if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                            authorizationService, httpContext.User, removedDestination.DestinationType, PermissionActionCode.Delete))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }

            // 1. Destinations first — self-contained, and they provision the inline secret whose reference the node needs.
            foreach (var spec in request.Destinations ?? [])
            {
                if (!nodes.TryGetValue(spec.NodeId, out var node))
                {
                    return ValidationBadRequest($"Destination spec references unknown node '{spec.NodeId}'.");
                }

                // Same per-type permission check as ConfigurationsController.AddDestinationConfiguration/
                // UpdateDestinationConfiguration — this is the actual path the Node Library uses to build a
                // workflow, so without this check here a role denied a destination type could still create
                // one of that type simply by going through the canvas instead of the Settings admin page.
                // ExistingId present means this spec updates an already-created destination (Edit); absent
                // means it's minting a brand-new one here (Create) — same branch those two endpoints use.
                var destinationAction = spec.ExistingId is null ? PermissionActionCode.Create : PermissionActionCode.Edit;
                if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                        authorizationService, httpContext.User, spec.Destination.DestinationType, destinationAction))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
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
                    return ValidationBadRequest($"Source spec references unknown node '{spec.NodeId}'.");
                }

                // Same per-vendor permission check as ConfigurationsController.AddSourceConnection/
                // UpdateSourceConnection — see the destinations loop above for why this has to be repeated
                // here rather than relying solely on that controller's check. Same Create-vs-Edit branch too.
                var sourceAction = spec.ExistingId is null ? PermissionActionCode.Create : PermissionActionCode.Edit;
                if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                        authorizationService, httpContext.User, spec.Source.SourceSystemType, sourceAction))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
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
                    return ValidationBadRequest($"Mapping spec references unknown node '{spec.NodeId}'.");
                }

                if (!TryResolveEntityId(spec.SourceNodeId, sourceIds, nodes, "sourceConnectionId", out var sourceConnectionId))
                {
                    return ValidationBadRequest(
                        $"Mapping spec '{spec.NodeId}' references source node '{spec.SourceNodeId}' with no created or referenced source connection.");
                }
                allReferencedSourceConnectionIds.Add(sourceConnectionId);

                if (!TryResolveEntityId(spec.DestinationNodeId, destinationIds, nodes, "destinationId", out var destinationId))
                {
                    return ValidationBadRequest(
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
                    return ValidationBadRequest(columnError);
                }

                // A mapping now lives on its node and nowhere else (plan §2.d): the node's own config already
                // carries every field, so an ordinary save writes NO MappingProfile. This loop is kept purely
                // for the column validation above, which is the only thing that catches a mapped column the
                // customer's table does not actually have before a run fails on it.
                //
                // Creating a profile here is what produced duplicate masters on every save — two named
                // "Patient" seconds apart — and left two sources of truth for one mapping, free to diverge. A
                // node holding 12 fields while its profile held 13 silently dropped the BirthDateAge column a
                // transformation rule targeted, so the rule had nothing to attach to.
                //
                // Masters are created only by an explicit "Mark as Master" in the wizard.
                //
                // The DESTINATION node still has to be told this resource type's write shape, though. It builds its
                // own MappingProfile at run time (DestinationNodeExecutor.CreateMappingProfiles) and uses that
                // profile's Fields to create/align the target table's columns — it does NOT read the mapping node's
                // config. With no master profile to fall back on either, leaving this unstamped means the executor
                // synthesizes a profile with zero fields and the write fails outright:
                // "Mapping profile for 'dbo.X' has no mapped fields — a SQL destination needs at least one mapped
                // column." Accumulated per destination node (never overwritten) so a destination fed by several
                // resource types keeps each one's own table and columns.
                if (!resourceMappingsByNode.TryGetValue(spec.DestinationNodeId, out var destinationResourceMappings))
                {
                    destinationResourceMappings = new Dictionary<string, DestinationResourceMappingConfig>(StringComparer.OrdinalIgnoreCase);
                    resourceMappingsByNode[spec.DestinationNodeId] = destinationResourceMappings;
                }

                destinationResourceMappings[spec.ResourceType] =
                    new DestinationResourceMappingConfig(spec.DestinationObject, spec.Fields);
            }

            // Re-stamp each mapping node's own inline "mappings" block from the (now repaired) specs.
            //
            // The client stamps this block itself, in the browser, BEFORE the request is sent
            // (WorkflowBuildAssemblerServiceV2.stampInlineMappings) — so it carries whatever JsonPath the wizard
            // had at that moment, naive fallbacks included. RepairMappingJsonPathsAsync above fixes
            // request.Mappings, which is what the destination node's resourceMappings is built from, but it
            // cannot retroactively fix a block the client already wrote. MappingNodeExecutor PREFERS this inline
            // block over every other source (see TryReadInlineResourceMapping), so leaving it stale means the
            // repaired paths never actually run: the destination node would hold "$.address[*].city" while the
            // node that does the mapping still held "$.address.city", and every array-nested column would keep
            // writing NULL. Rewritten from the same repaired specs so the two can never disagree.
            foreach (var specsByNode in (request.Mappings ?? []).GroupBy(spec => spec.NodeId, StringComparer.OrdinalIgnoreCase))
            {
                if (!nodes.TryGetValue(specsByNode.Key, out var mappingSpecNode))
                {
                    continue;
                }

                var inline = specsByNode.ToDictionary(
                    spec => spec.ResourceType,
                    spec => new DestinationResourceMappingConfig(spec.DestinationObject, spec.Fields),
                    StringComparer.OrdinalIgnoreCase);

                nodes[specsByNode.Key] = WithConfiguration(
                    mappingSpecNode,
                    config => config["mappings"] = JsonSerializer.SerializeToNode(inline, WebJsonOptions));
            }

            // Stamp each destination node's accumulated per-resource write shapes onto its own config. Carries the
            // fields themselves, not an id — same self-contained rule the mapping node follows (plan §2.d), so a
            // run can never depend on a master record that may have been edited or deleted since.
            foreach (var (destinationNodeId, resourceMappings) in resourceMappingsByNode)
            {
                if (!nodes.TryGetValue(destinationNodeId, out var destinationNode))
                {
                    continue;
                }

                nodes[destinationNodeId] = WithConfiguration(
                    destinationNode,
                    config => config["resourceMappings"] =
                        JsonSerializer.SerializeToNode(resourceMappings, WebJsonOptions));
            }

            // 3b. Stamp destinationId onto any mapping node the mappings loop above didn't touch. A whole-resource
            // FHIR destination (Medplum, FHIR Repository) writes the source resource verbatim and so carries NO field
            // mappings — request.Mappings has no spec for it, the loop never runs, and its mapping node is left
            // without a destinationId. The runtime MappingNodeExecutor resolves the destination TYPE from that id to
            // decide whether to emit whole-resource SourceJson carrier records; without it the mapping silently drops
            // every record and the run "succeeds" having written nothing. Derive it from the graph edge
            // mapping->destination and the destinations created above (never a hard-coded id); also carry the
            // sourceConnectionId from the source feeding the mapping node, mirroring what the mapping-spec path sets.
            const string mappingNodeType = "MappingNode"; // WorkflowNodeTypes.Mapping (Runtime.Application, not referenced here)
            foreach (var edge in request.Edges ?? [])
            {
                if (!destinationIds.TryGetValue(edge.ToNodeId, out var wholeResourceDestinationId)
                    || !nodes.TryGetValue(edge.FromNodeId, out var mappingNode)
                    || !string.Equals(mappingNode.NodeType, mappingNodeType, StringComparison.Ordinal)
                    || TryGetConfigurationGuid(mappingNode.ConfigurationJson, "destinationId", out _))
                {
                    continue;
                }

                nodes[edge.FromNodeId] = WithConfiguration(mappingNode, config =>
                {
                    config["destinationId"] = wholeResourceDestinationId.ToString();
                    foreach (var incoming in request.Edges!)
                    {
                        if (string.Equals(incoming.ToNodeId, edge.FromNodeId, StringComparison.OrdinalIgnoreCase)
                            && sourceIds.TryGetValue(incoming.FromNodeId, out var feedingSourceId))
                        {
                            config["sourceConnectionId"] = feedingSourceId.ToString();
                            break;
                        }
                    }
                });
            }

            // 4. Persist the graph carrying the injected references (original node order preserved).
            var definitionRequest = new WorkflowDefinitionRequest(
                request.Name,
                request.IsEnabled,
                request.Nodes.Select(node => nodes[node.Id]).ToArray(),
                request.Edges,
                request.Trigger,
                Description: request.Description);

            // Bump the version off whatever is currently stored (existingDefinition, loaded further up for the
            // node-removal check) so version history is real instead of always 1.
            var workflow = BuildWorkflow(
                request.WorkflowId ?? Guid.NewGuid(), definitionRequest, (existingDefinition?.Version ?? 0) + 1);
            await store.SaveAsync(workflow, cancellationToken);

            // Retire the mapping profiles / workflow-scoped rules this save just stopped referencing (a
            // destination removed from the canvas takes its Mapping / Transformation / De-identification
            // configuration with it). Soft deletes, so the rows stay resolvable for audit and lineage —
            // see IWorkflowConfigurationCleanupService. Judged against definitionRequest.Nodes, not
            // request.Nodes: the created-entity ids were injected into the former, so reading the raw
            // request would see a brand-new destination as "no destinationId" and retire the old one's
            // profiles even when this save is only re-pointing the same nodes. Inside the transaction, so
            // a later failure rolls the retirement back with everything else.
            await workflowConfigurationCleanup.SoftDeleteUnreferencedAsync(
                workflow.Id,
                existingDefinition,
                definitionRequest.Nodes.Select(node => node.ConfigurationJson).ToArray(),
                cancellationToken);

            // Everything above (destinations, sources, mappings, the workflow definition itself) is durable only
            // from this point on — nothing before here survives if any step failed or threw.
            await transaction.CommitAsync(cancellationToken);

            // Re-derive each referenced source connection's OAuth scopes from what every pipeline sharing it
            // actually consumes downstream, now that this save may have changed a destination's resource selection
            // (or introduced/removed a workflow referencing the connection). Union with sourceIds.Values (rather
            // than iterating sourceIds alone): a node using "Existing Source" unchanged never appears in sourceIds
            // (that dictionary is only ever populated by the request.Sources create/update loop above) — its
            // connection id is only ever resolved via TryResolveEntityId's node-config fallback inside the mappings
            // loop, captured into allReferencedSourceConnectionIds there. Without this union, a shared connection
            // reused via "Existing Source" would never get synced at all, no matter how many times its workflow is
            // rebuilt.
            var syncedScopes = new Dictionary<Guid, IReadOnlyList<string>>();
            foreach (var sourceConnectionId in allReferencedSourceConnectionIds.Union(sourceIds.Values).Distinct())
            {
                var scopes = await scopeSyncService.SyncAsync(sourceConnectionId, cancellationToken);
                if (scopes is not null)
                {
                    syncedScopes[sourceConnectionId] = scopes;
                }
            }

            var result = new WorkflowBuildResult(workflow.Id, sourceIds, destinationIds, mappingIds, syncedScopes);
            return Results.Created($"/api/v1/workflows/{workflow.Id}", result);
        // Workflow-module gate: create vs. edit is resolved inline above (WorkflowId present or not), since
        // a route-level policy is fixed at registration time and can't see the request body — this only
        // asserts "authenticated," same as every other endpoint below with no specific permission of its own.
        // Independent of, and enforced in addition to, the per-source/per-destination-type checks already
        // inside the loops above (see the HasPermissionAsync calls against spec.Source.SourceSystemType/
        // spec.Destination.DestinationType) — a role needs BOTH the workflow-level permission AND the
        // specific vendor/type's own Edit permission.
        }).RequireAuthorization();

        // Dry-run structural validation — never persists anything, but still requires being able to see/build
        // workflows at all (same reasoning as a preview of what create/edit would produce).
        // Module-access gate (workflow.view OR any workflow-node permission) — this is a view/preview action,
        // not a mutation, so it doesn't need the stricter workflow.create/edit + per-node check /build enforces.
        group.MapPost("/workflows/validate", (
            WorkflowDefinitionRequest request,
            IWorkflowGraphValidator validator) =>
        {
            var workflow = BuildWorkflow(Guid.NewGuid(), request);
            var result = validator.Validate(workflow);
            return Results.Ok(result);
        }).RequireAuthorization(AuthorizationPolicies.WorkflowModuleAccess);

        // Raw list (no summary/paging/facets) — superseded by /workflows/summary for the portal's list screen,
        // kept for any lower-level caller. Same module-access gate either way.
        group.MapGet("/workflows", async (
            IWorkflowDefinitionStore store,
            CancellationToken cancellationToken) =>
            Results.Ok(await store.ListAsync(cancellationToken)))
        .RequireAuthorization(AuthorizationPolicies.WorkflowModuleAccess);

        // Workflow-list screen: one summary row per workflow — shape, enabled state, last run, and the derived
        // action. The source node's referenced connection decides Launch (interactive SMART) vs Run (backend), so the
        // UI knows which endpoint to call. Admin-only (it reads source-connection configuration). Paging/search/sort
        // are applied server-side (see WorkflowSummaryPageDto) — the portal's workflow-list screen no longer slices
        // the full set client-side.
        group.MapGet("/workflows/summary", async (
            IWorkflowDefinitionStore store,
            IWorkflowRunStore runStore,
            IConfigurationRepository configurationRepository,
            IUserDisplayNameResolver userDisplayNameResolver,
            ICurrentUserService currentUserService,
            IUserPermissionsProvider userPermissionsProvider,
            CancellationToken cancellationToken,
            int page = 1,
            int pageSize = 20,
            string? search = null,
            string? sortColumn = null,
            string? sortDirection = null,
            string[]? statuses = null,
            string[]? applicationTypes = null,
            string[]? sourceSystemTypes = null) =>
        {
            var workflows = await store.ListAsync(cancellationToken);
            var sources = await configurationRepository.GetSourceConnectionsAsync(cancellationToken);
            var applicationTypeBySourceId = sources.ToDictionary(source => source.Id, source => source.ApplicationType);
            var systemTypeBySourceId = sources.ToDictionary(source => source.Id, source => source.SourceSystemType);
            var destinationTypeByDestinationId = (await configurationRepository.GetDestinationsAsync(cancellationToken))
                .ToDictionary(destination => destination.Id, destination => destination.DestinationType);

            // Row-level visibility: module access (the policy below) only proves the caller holds SOME
            // workflow/node permission — it says nothing about which specific workflows they should see.
            // A caller whose only grant is e.g. epic.view should see Epic-sourced workflows, not every
            // workflow regardless of vendor. Resolved once per request, not per row.
            var callerUserId = currentUserService.CurrentUser.UserId;
            var callerPermissions = callerUserId is null
                ? Array.Empty<string>()
                : await userPermissionsProvider.GetEffectivePermissionCodesAsync(callerUserId.Value, cancellationToken);
            var callerHasBlanketWorkflowAccess = HasGlobalWorkflowVisibility(callerPermissions);

            var summaries = new List<WorkflowSummaryDto>(workflows.Count);
            foreach (var workflow in workflows)
            {
                var usedGroups = new HashSet<PermissionGroupCode>();
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
                    if (systemTypeBySourceId.TryGetValue(sourceId, out var sourceVendorType))
                    {
                        AddVendorGroupIfSpecific(usedGroups, sourceVendorType);
                    }
                    if (applicationTypeBySourceId.TryGetValue(sourceId, out var type) && type is not null
                        && (applicationType is null || type.Value > applicationType.Value))
                    {
                        applicationType = type;
                        launchSourceId = sourceId;
                    }
                }

                var isLaunch = applicationType is ApplicationType.EhrLaunch or ApplicationType.Standalone or ApplicationType.Patient;

                // A destination node that is present but not yet wired to a destination record does not count:
                // the workflow still has nowhere to write, so it is not Ready. WorkflowDefinition.HasDestination
                // asks the weaker "is a Destination node present" question for callers that only have the graph;
                // the loop below answers the stronger one — and, in the same pass, collects the vendor groups the
                // permission filter after it needs — so both the status and the response flag read off it.
                var hasConfiguredDestination = false;
                foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Destination))
                {
                    if (!TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out var destinationId))
                    {
                        continue;
                    }

                    hasConfiguredDestination = true;
                    if (destinationTypeByDestinationId.TryGetValue(destinationId, out var destinationVendorType))
                    {
                        AddVendorGroupIfSpecific(usedGroups, destinationVendorType);
                    }
                }

                // Skip rows the caller has no vendor permission for and no blanket workflow.* grant either —
                // module access alone (any single node permission) only proves they belong on this page at
                // all, not that every workflow in the system is theirs to see.
                //
                // A workflow that names no vendor-specific source or destination yet (usedGroups empty) is not
                // filtered: that is what a just-created workflow looks like before its canvas is ever saved, and
                // hiding it would drop the row the user is about to open. Same reasoning as the by-id read.
                if (usedGroups.Count > 0
                    && !callerHasBlanketWorkflowAccess
                    && !usedGroups.Any(group => HasAnyActionFor(callerPermissions, group)))
                {
                    continue;
                }

                var status = !workflow.IsEnabled ? nameof(WorkflowLifecycleStatus.Disabled)
                    : hasConfiguredDestination ? nameof(WorkflowLifecycleStatus.Ready)
                    : nameof(WorkflowLifecycleStatus.Draft);

                var runs = await runStore.ListByDefinitionAsync(workflow.Id, cancellationToken);
                var lastRun = runs.OrderByDescending(run => run.StartedAt).FirstOrDefault();

                var resolvedSourceId = launchSourceId ?? firstSourceId;
                var sourceSystemType = resolvedSourceId is { } id && systemTypeBySourceId.TryGetValue(id, out var systemType)
                    ? systemType.ToString()
                    : null;

                summaries.Add(new WorkflowSummaryDto(
                    workflow.Id,
                    workflow.Name,
                    status,
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
                    hasConfiguredDestination,
                    workflow.IsPubliclyLaunchable,
                    workflow.CreatedOnUtc,
                    workflow.CreatedBy,
                    workflow.UpdatedOnUtc,
                    workflow.UpdatedBy,
                    workflow.Description,
                    workflow.WorkflowNumber));
            }

            // Resolve each summary's CreatedBy/ModifiedBy (a stored Users.Id GUID, or an older/pre-conversion
            // string) to a display name in one batched lookup, before filtering/paging.
            var actorNames = await userDisplayNameResolver.ResolveAsync(
                summaries.SelectMany(summary => new[] { summary.CreatedBy, summary.ModifiedBy }), cancellationToken);
            summaries = summaries.Select(summary => summary with
            {
                CreatedBy = summary.CreatedBy is { } createdBy ? actorNames.GetValueOrDefault(createdBy, createdBy) : null,
                ModifiedBy = summary.ModifiedBy is { } modifiedBy ? actorNames.GetValueOrDefault(modifiedBy, modifiedBy) : null,
            }).ToList();

            // Facet option lists reflect the full unfiltered set (not `matching`) so unchecking every box in one
            // category doesn't make the other categories' checkboxes disappear out from under the user.
            var availableStatuses = summaries.Select(s => s.Status)
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToArray();
            var availableApplicationTypes = summaries.Select(s => s.ApplicationType).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();
            var availableSourceSystemTypes = summaries.Select(s => s.SourceSystemType).OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToArray();

            IEnumerable<WorkflowSummaryDto> matching = summaries;
            if (!string.IsNullOrWhiteSpace(search))
            {
                matching = matching.Where(summary =>
                    summary.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                    || (summary.ApplicationType?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    || (summary.Description?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
                    // Without this, pasting a workflow number read off a ticket or an email returns nothing —
                    // which defeats the point of having a quotable id at all.
                    || (summary.WorkflowNumber?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false));
            }

            if (statuses is { Length: > 0 })
            {
                var statusSet = new HashSet<string>(statuses, StringComparer.OrdinalIgnoreCase);
                matching = matching.Where(summary => statusSet.Contains(summary.Status));
            }

            if (applicationTypes is { Length: > 0 })
            {
                var applicationTypeSet = new HashSet<string>(applicationTypes, StringComparer.OrdinalIgnoreCase);
                matching = matching.Where(summary => summary.ApplicationType is not null && applicationTypeSet.Contains(summary.ApplicationType));
            }

            if (sourceSystemTypes is { Length: > 0 })
            {
                var sourceSystemTypeSet = new HashSet<string>(sourceSystemTypes, StringComparer.OrdinalIgnoreCase);
                matching = matching.Where(summary => summary.SourceSystemType is not null && sourceSystemTypeSet.Contains(summary.SourceSystemType));
            }

            var sorted = SortSummaries(matching, sortColumn, sortDirection).ToArray();

            var effectivePage = Math.Max(1, page);
            var effectivePageSize = Math.Clamp(pageSize, 1, 200);
            var pageItems = sorted
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToArray();

            return Results.Ok(new WorkflowSummaryPageDto(
                pageItems, sorted.Length, availableStatuses, availableApplicationTypes, availableSourceSystemTypes));
        // Workflow-module access gate (workflow.view OR any workflow-node permission) — can this role view
        // the Workflows list at all. This is the real data source behind the Workflows page.
        }).RequireAuthorization(AuthorizationPolicies.WorkflowModuleAccess);

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

        // Mapping Profiles master screen: which mapping profile ids are referenced right now, either by a workflow
        // node's config (mappingProfileId / the per-resource mappingProfileIds map — see BuildWorkflow above) or by
        // a persisted ResourcePipelineRoute (primary mapping, a composite ResourceMappings entry, or a parent
        // reference target). The route-level FK is already Restrict, but that only surfaces as a raw DB error at
        // delete time — this gives the list screen an accurate "used by N workflows" count up front, and the delete
        // endpoint (ConfigurationsController.DeleteMappingProfile) checks the route-level usage itself as the actual
        // delete guard.
        group.MapGet("/workflows/mapping-profile-usage", async (
            IWorkflowDefinitionStore store,
            IConfigurationRepository configurationRepository,
            CancellationToken cancellationToken) =>
        {
            var workflows = await store.ListAsync(cancellationToken);

            var usedFromNodeConfig = workflows
                .SelectMany(workflow => workflow.Nodes)
                .SelectMany(node => GetMappingProfileIdsFromConfiguration(node.ConfigurationJson))
                .Distinct();

            var routes = await configurationRepository.GetRoutesAsync(cancellationToken);
            var usedFromRoutes = routes
                .SelectMany(route => route.ResourceMappings
                    .SelectMany(mapping => mapping.ParentReferences
                        .Select(parent => parent.ParentMappingProfileId)
                        .Append(mapping.MappingProfileId))
                    .Append(route.MappingProfileId));

            var usedMappingProfileIds = usedFromNodeConfig.Concat(usedFromRoutes).Distinct().ToArray();

            return Results.Ok(usedMappingProfileIds);
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
            string? callerId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            IAllowedCorsOriginsCache allowedCorsOriginsCache,
            IGovernanceLogger governanceLogger,
            CancellationToken cancellationToken) =>
        {
            // Same REQUIRED callerId gate as /workflows/{id}/latest-launch-result below, and for the same reason:
            // this response is PHI-bearing and the endpoint is unauthenticated, so an absent callerId must be a
            // refusal rather than a skipped check — otherwise the gate is bypassable by omitting one query
            // parameter. A run id is not a credential either; it travels through URLs, configs and support
            // tickets. This guard was missing here while its sibling had it, which left a full Patient resource
            // readable by anyone who knew (or guessed) a run id.
            if (string.IsNullOrWhiteSpace(callerId)
                || !await CallerIdOriginValidator.IsAllowedOriginAsync(callerId, allowedCorsOriginsCache, cancellationToken))
            {
                await LogRefusedLaunchResultAsync(
                    governanceLogger,
                    workflowRunId,
                    string.IsNullOrWhiteSpace(callerId)
                        ? "Refused: callerId is required (must be an allowed origin)."
                        : "Refused: callerId is not an allowed origin.",
                    cancellationToken);
                return Results.NotFound();
            }

            var payloads = await recorder.GetPagedAsync(workflowRunId, page: 1, pageSize: 500, cancellationToken);
            var resourceBatches = payloads.Items.Where(item => item.Contract == "ResourceBatch");

            // Aggregate ACROSS every ResourceBatch this run recorded (the source node emits one per resource type /
            // page). These counts are read straight off the stored ResourceTypeCountsJson metadata — the resources
            // themselves are no longer persisted, so there is nothing here to parse or decrypt.
            var resourceCounts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var batch in resourceBatches)
            {
                if (string.IsNullOrWhiteSpace(batch.ResourceTypeCountsJson))
                {
                    continue;
                }

                var counts = JsonNode.Parse(batch.ResourceTypeCountsJson) as JsonObject;
                if (counts is null)
                {
                    continue;
                }

                foreach (var entry in counts)
                {
                    var value = entry.Value?.GetValue<int>() ?? 0;
                    resourceCounts[entry.Key] = resourceCounts.TryGetValue(entry.Key, out var current)
                        ? current + value
                        : value;
                }
            }

            return Results.Ok(new
            {
                // Per-resource-type counts of everything this run's source node fetched — powers the "resources
                // fetched" list on the demo's launch view.
                //
                // The `patient`/`patientId` fields this used to return are gone: serving a whole Epic Patient
                // resource meant retaining it, and retained PHI is PHI whether or not it is encrypted at rest.
                // A caller that needs the patient reads it from the destination the run wrote to.
                resourceCounts,
            });
        });

        // Anonymous, read-only companion to the launch-result endpoint above for a third-party app that knows only
        // the workflow id, not a specific run id — resolves the workflow's most recent run that actually produced a
        // Patient and returns that Patient (same shape), so the app can show the latest fetched patient WITHOUT a
        // fresh EHR launch. Gated on IsPubliclyLaunchable (the same public-launch opt-in the anonymous launch
        // endpoints require) so an arbitrary caller can't read any workflow's data by id. Strictly read-only —
        // reflects what the source last fetched from Execution History; it never triggers a new run.
        group.MapGet("/workflows/{workflowId:guid}/latest-launch-result", async (
            Guid workflowId,
            string? callerId,
            IWorkflowDefinitionStore store,
            IWorkflowRunStore runStore,
            IWorkflowNodeResourceHistoryRecorder recorder,
            IAllowedCorsOriginsCache allowedCorsOriginsCache,
            IGovernanceLogger governanceLogger,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null || !workflow.IsPubliclyLaunchable)
            {
                await LogRefusedLatestLaunchResultAsync(
                    governanceLogger,
                    workflowId,
                    workflow is null
                        ? "Refused: no workflow with this id exists."
                        : "Refused: workflow is not opted into public launch (POST /workflows/{id}/enable-public-launch).",
                    cancellationToken);
                return Results.NotFound();
            }

            // This endpoint returns a Patient resource to an UNAUTHENTICATED caller, so — unlike the launch-url
            // minting endpoints, where an absent callerId simply skips the check — callerId is REQUIRED here and
            // must name an allowed origin. Treating "no callerId" as "no check" would leave the whole gate
            // bypassable by omitting one query parameter, which for a PHI-bearing response is no gate at all.
            // The workflow id alone is not a credential: it travels through URLs, configs and support tickets.
            if (string.IsNullOrWhiteSpace(callerId)
                || !await CallerIdOriginValidator.IsAllowedOriginAsync(callerId, allowedCorsOriginsCache, cancellationToken))
            {
                await LogRefusedLatestLaunchResultAsync(
                    governanceLogger,
                    workflowId,
                    string.IsNullOrWhiteSpace(callerId)
                        ? "Refused: callerId is required (must be an allowed origin)."
                        : "Refused: callerId is not an allowed origin.",
                    cancellationToken);
                return Results.NotFound();
            }

            var runs = (await runStore.ListByDefinitionAsync(workflowId, cancellationToken))
                .OrderByDescending(run => run.StartedAt);

            foreach (var run in runs)
            {
                var payloads = await recorder.GetPagedAsync(run.Id, page: 1, pageSize: 50, cancellationToken);
                var sourcePayload = payloads.Items.FirstOrDefault(item => item.Contract == "ResourceBatch");
                if (sourcePayload is null)
                {
                    continue;
                }

                // Metadata only. This endpoint used to return the whole Patient resource, which required retaining
                // it — retained PHI is PHI whether or not it is encrypted at rest, so the resources are no longer
                // stored. What remains answerable is "which run fetched a Patient, and how many of each type" —
                // a caller needing the patient itself reads it from the destination the run wrote to.
                if (string.IsNullOrWhiteSpace(sourcePayload.ResourceTypeCountsJson))
                {
                    continue;
                }

                var counts = JsonNode.Parse(sourcePayload.ResourceTypeCountsJson) as JsonObject;
                if (counts is null || !counts.ContainsKey("Patient"))
                {
                    continue;
                }

                return Results.Ok(new
                {
                    workflowRunId = run.Id,
                    resourceCounts = counts,
                });
            }

            return Results.Ok(new { workflowRunId = (Guid?)null, resourceCounts = (JsonObject?)null });
        });

        // Loads one workflow's full graph — the builder canvas's "open workflow" call. View-only: this must
        // stay reachable with just workflow.view so a view-only role can open and look at an existing workflow.
        group.MapGet("/workflows/{workflowId:guid}", async (
            Guid workflowId,
            IWorkflowDefinitionStore store,
            IConfigurationRepository configurationRepository,
            ICurrentUserService currentUserService,
            IUserPermissionsProvider userPermissionsProvider,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            // Same row-level visibility as /workflows/summary (see the shared helpers below) — a workflow
            // hidden from the list must not be reachable by opening its id directly either, otherwise
            // "hidden from the list" is cosmetic only. Module access (the policy below) already proved the
            // caller holds SOME workflow/node permission; this checks it's one this specific workflow uses.
            var userId = currentUserService.CurrentUser.UserId;
            var permissions = userId is null
                ? Array.Empty<string>()
                : await userPermissionsProvider.GetEffectivePermissionCodesAsync(userId.Value, cancellationToken);

            if (!HasGlobalWorkflowVisibility(permissions))
            {
                var usedGroups = new HashSet<PermissionGroupCode>();
                foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Source))
                {
                    if (TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceId))
                    {
                        var source = await configurationRepository.GetSourceConnectionAsync(sourceId, cancellationToken);
                        if (source is not null) AddVendorGroupIfSpecific(usedGroups, source.SourceSystemType);
                    }
                }
                foreach (var node in workflow.Nodes.Where(node => node.Category == WorkflowNodeCategory.Destination))
                {
                    if (TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out var destinationId))
                    {
                        var destination = await configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
                        if (destination is not null) AddVendorGroupIfSpecific(usedGroups, destination.DestinationType);
                    }
                }

                // An empty set means this workflow names no vendor-specific source or destination yet — which
                // is exactly what a workflow looks like between "New" creating it (name + description, no
                // canvas) and its first save. There is no vendor permission to check against, so filtering on
                // one hides the workflow the caller just created: the builder opens in edit mode and reads
                // back a 404, rendering blank name and description fields over a record that does exist.
                // Absence of a vendor is not evidence of a vendor the caller lacks.
                if (usedGroups.Count > 0 && !usedGroups.Any(group => HasAnyActionFor(permissions, group)))
                {
                    return Results.NotFound();
                }
            }

            return Results.Ok(workflow);
        // Module-access gate (workflow.view OR any workflow-node permission) — opening a single workflow to
        // view it, same as the list endpoints above. Row-level vendor visibility is layered on top inside
        // the handler itself (see above) since it depends on this specific workflow's own nodes.
        }).RequireAuthorization(AuthorizationPolicies.WorkflowModuleAccess);

        // Downloadable plain-text dump of every configuration table this workflow depends on — the graph tables
        // plus every source/destination/mapping/route/rule row they reference — each section carrying the SELECT
        // that produced it and its rows printed one data point per line, for offline analysis and support triage.
        // Returns text/plain as an attachment rather than JSON: the caller is a human reading a file, not code.
        // SuperAdmin-only, NOT the workflow-module gate the rest of these endpoints use: this returns the
        // whole configuration surface in one file — connection endpoints, Key Vault references, mapping and
        // rule rows across every table the workflow touches — which is far more than the caller sees through
        // any individual screen, and is useful to an attacker as a map even with secret VALUES excluded.
        // UnifiedAdmin would also admit a tenant Admin; this is deliberately the stricter role check, and it
        // matches the portal's own canViewConfiguration gate on the menu item that calls it.
        group.MapGet("/workflows/{workflowId:guid}/configuration-export", async (
            Guid workflowId,
            IWorkflowConfigurationExporter exporter,
            CancellationToken cancellationToken) =>
        {
            var export = await exporter.ExportAsync(workflowId, cancellationToken);
            return export is null
                ? Results.NotFound()
                : Results.File(System.Text.Encoding.UTF8.GetBytes(export.Content), "text/plain; charset=utf-8", export.FileName);
        }).RequireAuthorization(AuthorizationPolicies.SuperAdminOnly);

        // Low-level upsert-by-id — superseded by /workflows/build for the portal's builder canvas (which also
        // provisions source/destination/mapping records), kept for any lower-level caller. Always modifies
        // whatever workflow already has this id, so it's workflow.edit regardless of caller.
        // NOTE: unlike /workflows/build, this route still has no per-vendor Create/Edit check for a node this
        // request adds or changes — that's a pre-existing gap, out of scope for the node-removal fix below,
        // which only closes the ONE bypass this task asked about (removing a node without that vendor's own
        // .delete permission). A caller with only workflow.edit could still use this route to add/edit a node
        // of any vendor with no vendor-specific check at all; flagging this separately rather than expanding
        // this change to cover it too.
        group.MapPut("/workflows/{workflowId:guid}", async (
            Guid workflowId,
            WorkflowDefinitionRequest request,
            IWorkflowDefinitionStore store,
            IWorkflowConfigurationCleanupService workflowConfigurationCleanup,
            IConfigurationService configurationService,
            IConfigurationRepository configurationRepository,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var existing = await store.GetAsync(workflowId, cancellationToken);

            // Same node-removal check as /workflows/build — see the detailed comment there. Kept in sync
            // deliberately rather than factored into one shared helper, matching how the rest of this file
            // already tolerates small duplication between the two save paths (e.g. BuildWorkflow's own
            // version-bump logic) rather than adding shared-helper indirection for a two-call-site rule.
            if (existing is not null)
            {
                var newSourceConnectionIds = CollectConfigurationGuids(request.Nodes.Select(n => n.ConfigurationJson), "sourceConnectionId");
                foreach (var removedSourceConnectionId in CollectConfigurationGuids(existing.Nodes.Select(n => n.ConfigurationJson), "sourceConnectionId").Except(newSourceConnectionIds))
                {
                    var removedSource = await configurationService.GetSourceConnectionByIdAsync(removedSourceConnectionId, cancellationToken);
                    if (removedSource is null) continue;
                    if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                            authorizationService, httpContext.User, removedSource.SourceSystemType, PermissionActionCode.Delete))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }

                var newDestinationIds = CollectConfigurationGuids(request.Nodes.Select(n => n.ConfigurationJson), "destinationId");
                foreach (var removedDestinationId in CollectConfigurationGuids(existing.Nodes.Select(n => n.ConfigurationJson), "destinationId").Except(newDestinationIds))
                {
                    var removedDestination = await configurationRepository.GetDestinationAsync(removedDestinationId, cancellationToken);
                    if (removedDestination is null) continue;
                    if (!await ControllerAuthorizationExtensions.HasPermissionAsync(
                            authorizationService, httpContext.User, removedDestination.DestinationType, PermissionActionCode.Delete))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }
                }
            }

            var workflow = BuildWorkflow(workflowId, request, (existing?.Version ?? 0) + 1);
            await store.SaveAsync(workflow, cancellationToken);

            // Same retirement pass as /workflows/build — see the comment there. This path provisions
            // nothing, so the request's own node configuration already carries the final ids.
            await workflowConfigurationCleanup.SoftDeleteUnreferencedAsync(
                workflowId,
                existing,
                request.Nodes.Select(node => node.ConfigurationJson).ToArray(),
                cancellationToken);

            return Results.Ok(workflow);
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Edit)));

        // Duplicates an existing workflow definition under a new name. Every referenced SourceConnection/
        // DestinationConfiguration/MappingProfile is deep-cloned into its OWN new row (see CloneSourceConnectionAsync/
        // CloneDestinationConfigurationAsync/CloneMappingProfileAsync below) and each node's ConfigurationJson is
        // rewritten to point at the clones — copying the JSON verbatim (the previous behavior) left the copy silently
        // sharing the exact same backing connections as the original, so editing one (e.g. the source's audience) on
        // either workflow changed what BOTH displayed, since the workflow list re-derives that display live from
        // whichever SourceConnection row the node's id currently resolves to. New Guids throughout (workflow id +
        // every node/edge id) via the same BuildWorkflow path every other create/save uses.
        group.MapPost("/workflows/{workflowId:guid}/copy", async (
            Guid workflowId,
            CopyWorkflowRequest request,
            IWorkflowDefinitionStore store,
            IConfigurationRepository configurationRepository,
            IConfigurationService configurationService,
            ISecretProvider secretProvider,
            ISecretWriter secretWriter,
            ISourceApplicationStrategyRegistry applicationStrategies,
            IAuthorizationService authorizationService,
            HttpContext httpContext,
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

            // A copy always mints a brand-new workflow id (see BuildWorkflow(Guid.NewGuid(), ...) below) — same
            // as every other create path, the workflow quota is enforced centrally by
            // LicenseEnforcementSaveChangesInterceptor, not here.

            // Same all-or-nothing rationale as /workflows/build: several entities get created below before the
            // workflow definition itself is saved, and a failure partway through (a validation rejection on the
            // cloned mapping profile, say) must not leave an orphaned SourceConnection/DestinationConfiguration
            // clone behind with no workflow ever pointing at it.
            await using var transaction = await configurationRepository.BeginTransactionAsync(cancellationToken);

            // Keyed by the ORIGINAL entity id, so an entity referenced by more than one node (or re-referenced in a
            // mapping node's own sourceConnectionId/destinationId re-resolution fields) is only ever cloned once.
            var clonedSourceConnectionIds = new Dictionary<Guid, (Guid Id, ApplicationType? ApplicationType)>();
            var clonedDestinations = new Dictionary<Guid, (Guid Id, string KeyVaultName, string SecretName)>();
            var clonedMappingProfileIds = new Dictionary<Guid, Guid>();

            // 1. Source nodes first — nothing else depends on anything BUT these ids, and mapping profiles below
            // need the clone's new id to repoint their own SourceConnectionId onto.
            foreach (var node in source.Nodes.Where(n => n.Category == WorkflowNodeCategory.Source))
            {
                if (TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceConnectionId))
                {
                    // Same per-vendor permission check as the /workflows/build sources loop — cloning a
                    // workflow creates a brand-new SourceConnection row of the same vendor as the original
                    // (see CloneSourceConnectionAsync), so it's subject to the same "can this role create a
                    // connection of this vendor" rule. Resolved from the original connection's own vendor,
                    // since the copy request itself carries no vendor information.
                    var originalSource = await configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
                    if (originalSource is not null
                        && !await ControllerAuthorizationExtensions.HasPermissionAsync(
                            authorizationService, httpContext.User, originalSource.SourceSystemType, PermissionActionCode.Create))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    await CloneSourceConnectionAsync(
                        sourceConnectionId, clonedSourceConnectionIds, configurationRepository, configurationService,
                        secretProvider, secretWriter, cancellationToken);
                }
            }

            // 2. Destination nodes — same reasoning as Source nodes above.
            foreach (var node in source.Nodes.Where(n => n.Category == WorkflowNodeCategory.Destination))
            {
                if (TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out var destinationId))
                {
                    // Same per-type permission check as the /workflows/build destinations loop — see the
                    // source-node comment above for why this is resolved from the original entity's own type.
                    var originalDestination = await configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
                    if (originalDestination is not null
                        && !await ControllerAuthorizationExtensions.HasPermissionAsync(
                            authorizationService, httpContext.User, originalDestination.DestinationType, PermissionActionCode.Create))
                    {
                        return Results.StatusCode(StatusCodes.Status403Forbidden);
                    }

                    await CloneDestinationConfigurationAsync(
                        destinationId, clonedDestinations, configurationRepository, configurationService,
                        secretProvider, secretWriter, cancellationToken);
                }
            }

            // 3. Mapping/Transform nodes — clone every MappingProfile they reference (legacy single id + the
            // per-resource map both), pointed at the CLONED source/destination ids resolved above.
            foreach (var node in source.Nodes.Where(n => n.Category == WorkflowNodeCategory.Transform))
            {
                foreach (var mappingProfileId in GetMappingProfileIdsFromConfiguration(node.ConfigurationJson).Distinct())
                {
                    await CloneMappingProfileAsync(
                        mappingProfileId, clonedMappingProfileIds, clonedSourceConnectionIds, clonedDestinations,
                        configurationRepository, configurationService, cancellationToken);
                }
            }

            // 4. Rewrite every node's ConfigurationJson to point at the clones instead of the originals — the
            // node's OWN category-specific id (sourceConnectionId/destinationId) plus mappingProfileId/
            // mappingProfileIds and the mapping node's own re-resolution copies of sourceConnectionId/destinationId.
            var nodeRequests = source.Nodes
                .Select(node => WithConfiguration(
                    new WorkflowNodeRequest(
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
                        node.CheckpointUrlEnabled),
                    config =>
                    {
                        if (config["sourceConnectionId"]?.ToString() is { } rawSourceId
                            && Guid.TryParse(rawSourceId, out var originalSourceId)
                            && clonedSourceConnectionIds.TryGetValue(originalSourceId, out var clonedSource))
                        {
                            config["sourceConnectionId"] = clonedSource.Id.ToString();
                            // "Epic audience" is a node-local cache the Angular EHR-vendor source form writes at
                            // save time and reads straight back to pre-populate its own dropdown on reopen —
                            // NEVER re-derived from the live SourceConnection.ApplicationType (see
                            // portal/src/app/services/wizard.service.ts's open()/APPLICATION_TYPE_TO_AUDIENCE).
                            // Left untouched, a copy would keep showing whatever audience the ORIGINAL workflow's
                            // node happened to have cached — which can genuinely differ from the clone's own,
                            // correctly-cloned SourceConnection.ApplicationType above. Only rewritten when the key
                            // is already present, so a non-Epic source node (which never had this key) doesn't
                            // gain a bogus one.
                            if (config.ContainsKey("Epic audience"))
                            {
                                config["Epic audience"] = ApplicationTypeToEpicAudienceSlug(applicationStrategies, clonedSource.ApplicationType);
                            }
                        }

                        if (config["destinationId"]?.ToString() is { } rawDestinationId
                            && Guid.TryParse(rawDestinationId, out var originalDestinationId)
                            && clonedDestinations.TryGetValue(originalDestinationId, out var clonedDestination))
                        {
                            config["destinationId"] = clonedDestination.Id.ToString();
                            config["secretKeyVaultName"] = clonedDestination.KeyVaultName;
                            config["secretName"] = clonedDestination.SecretName;
                        }

                        // A de-identification policy belongs to ONE workflow — that is the whole reason it moved
                        // off the destination. Carried over verbatim, the copy would point at the original's
                        // policy and every rule added on the copy would change what the ORIGINAL redacts, with
                        // nothing on either screen saying so. Dropped rather than cloned: the copy's own
                        // De-identification tab mints a fresh policy the first time a rule is added there (see
                        // FieldMappingListComponent.ensureWorkflowProfile$), named after the copy's own id.
                        //
                        // What the copy then redacts under: the cloned DestinationConfiguration carries no policy
                        // either (CreateDestinationConfigurationRequest has no such field), so a copied
                        // De-identification node falls all the way through ResolveProfileIdAsync to the seeded
                        // Safe Harbor default. So the copy is NOT unredacted — it redacts under the default
                        // rather than under the original's policy, until rules are authored on it. That is the
                        // right failure for a copy: sharing would silently change what a workflow the user never
                        // opened redacts, and passing PHI through unredacted would be worse than either.
                        //
                        // BOTH keys, because they live on different nodes and only one of them is what actually
                        // runs: the destination node carries "deIdentificationProfileId" (authoring state), while
                        // the De-identification node carries "profileId", which is what
                        // DeIdentificationNodeExecutor.ResolveProfileIdAsync reads. Clearing only the first would
                        // leave the copy still redacting under the original's policy at run time while every
                        // screen showed it as having none.
                        config.Remove("deIdentificationProfileId");
                        config.Remove("profileId");

                        if (config["mappingProfileId"]?.ToString() is { } rawMappingId
                            && Guid.TryParse(rawMappingId, out var originalMappingId)
                            && clonedMappingProfileIds.TryGetValue(originalMappingId, out var newMappingId))
                        {
                            config["mappingProfileId"] = newMappingId.ToString();
                        }

                        if (config["mappingProfileIds"] is JsonObject idsByResource)
                        {
                            var rewritten = new JsonObject();
                            foreach (var entry in idsByResource)
                            {
                                rewritten[entry.Key] = entry.Value?.ToString() is { } rawId
                                    && Guid.TryParse(rawId, out var parsedId)
                                    && clonedMappingProfileIds.TryGetValue(parsedId, out var mappedId)
                                        ? mappedId.ToString()
                                        : entry.Value?.DeepClone();
                            }
                            config["mappingProfileIds"] = rewritten;
                        }
                    }))
                .ToArray();

            var edgeRequests = source.Edges
                .Select(edge => new WorkflowEdgeRequest(edge.FromNodeId.ToString(), edge.ToNodeId.ToString()))
                .ToArray();

            var triggerRequest = source.Trigger is { } trigger
                ? new WorkflowTriggerRequest(trigger.Type, trigger.ScheduleExpression, trigger.IntervalMinutes, trigger.BackfillOnFirstRun, trigger.TimeZoneId)
                : null;

            // Always created disabled, regardless of the source's enabled state: an enabled Schedule/Poll trigger
            // firing immediately — in parallel with the original, against the same source/destination — would
            // double-run and double-write before the user has even reviewed the copy.
            // A description the caller didn't supply at all (null) is inherited from the original — the copy
            // describes the same pipeline; an explicitly empty one is honored as "no description".
            var definitionRequest = new WorkflowDefinitionRequest(
                name, IsEnabled: false, nodeRequests, edgeRequests, triggerRequest,
                Description: request.Description ?? source.Description);
            var copy = BuildWorkflow(Guid.NewGuid(), definitionRequest);
            await store.SaveAsync(copy, cancellationToken);

            // Everything above (cloned source/destination/mapping entities, the workflow definition itself) is
            // durable only from this point on — nothing before here survives if any step failed or threw.
            await transaction.CommitAsync(cancellationToken);

            return Results.Created($"/api/v1/workflows/{copy.Id}", copy);
        // Workflow-module gate: copying produces a brand-new workflow (+ cloned Source/Destination rows),
        // so it's gated the same as build — workflow.create, independent of and in addition to the existing
        // per-vendor/per-type checks in the clone loops above.
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Create)));

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
        // Module-access gate (workflow.view OR any workflow-node permission) — same reasoning as the
        // draft-validate endpoint above.
        }).RequireAuthorization(AuthorizationPolicies.WorkflowModuleAccess);

        // Pre-flight for a run: checks the caller's parameters, trims them, and records the attempt — BEFORE any
        // token is touched or any EHR call is made. Always creates an Execution History row, valid or not, which is
        // the point: a refused attempt previously left no trace anywhere (no run, no outbound call, no exception),
        // so "I clicked Fetch and Execution History is empty" had no answer.
        //
        // The correlation id it returns is DERIVED, not minted (see WorkflowCorrelationId): the caller does not
        // have to echo it back, because every later leg of the same attempt — token-status, the launch-url mint,
        // /oauth/callback, /run — recomputes the same value from the workflow id and the session id it already
        // sends. That keeps the third-party contract at "call this first", with nothing to thread through an
        // OAuth round trip.
        //
        // Anonymous, matching the run/token-status/discard-token endpoints this precedes: an interactive caller
        // has no FHIRBridge session, and this reveals nothing a caller that already knows the workflow id cannot
        // learn by simply attempting the run.
        group.MapPost("/workflows/{workflowId:guid}/validate-run", async (
            Guid workflowId,
            WorkflowRunRequest? request,
            IWorkflowDefinitionStore store,
            IWorkflowRunValidator runValidator,
            IWorkflowRunStore runStore,
            ICurrentUserService currentUserService,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            // Normalized once, here: the run created below carries these values, so validation and execution can
            // never disagree about what was actually supplied.
            var parameters = WorkflowRunParameters.Normalize(
                request?.PatientId, request?.PatientSearchCriteria, request?.CallerId, request?.EhrEndpointId);

            // Mint a fresh, ATTEMPT-scoped correlation id here — this is the moment an attempt begins, and this id
            // is what the caller echoes back (as X-Correlation-Id) on every later call in the same attempt, and
            // what rides inside the encrypted launch context across the EHR redirect that no header survives.
            //
            // Deliberately random rather than derived from the workflow and caller session: a derived id is
            // necessarily constant for the whole browser session, so every attempt against one workflow collapses
            // onto it — two clicks an hour apart became one execution. Derivation remains only as the fallback for
            // callers that never call validate-run at all.
            //
            // An explicitly supplied id still wins, so a caller that manages its own correlation keeps doing so.
            var correlationId = request?.CorrelationId
                ?? httpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                ?? $"{WorkflowCorrelationId.Prefix}{Guid.NewGuid():N}";

            // Everything this request goes on to write — the run row below, the inbound API request log, any
            // governance capture — resolves its correlation id from the ambient request, so stamp before any of it.
            WorkflowCorrelationResolver.ApplyExplicit(httpContext, correlationId);

            var errors = await runValidator.ValidateAsync(workflow, parameters, cancellationToken);

            // Re-validating the SAME attempt updates its existing row rather than opening another — a Standalone
            // fetch legitimately validates twice: once on the click, once on the auto-retry after the EHR sign-in.
            // Because the id above is attempt-scoped, an exact match here IS the same attempt; no time heuristic is
            // needed to tell one click from another (which is what a session-derived id would have required).
            // Only a Validated row is reused: once a run has executed or been refused, the next click gets its own.
            var existingRun = await runStore.FindValidatedAsync(workflow.Id, correlationId, cancellationToken);

            var workflowRun = new WorkflowRun(
                existingRun?.Id ?? Guid.NewGuid(),
                workflow.Id,
                existingRun?.StartedAt ?? DateTimeOffset.UtcNow,
                triggeredBy: currentUserService.CurrentUser.AuditName,
                triggerType: "ValidateRun",
                workflowDefinitionVersion: workflow.Version,
                correlationId: correlationId);

            if (errors.Count == 0)
            {
                workflowRun.MarkValidated();
            }
            else
            {
                workflowRun.FailValidation(
                    string.Join(" | ", errors.Select(error => $"{error.Parameter}: {error.Message}")),
                    DateTimeOffset.UtcNow);
            }

            await runStore.SaveAsync(workflowRun, cancellationToken);

            // 200 with isValid=false, not 400: the request itself was well-formed and was successfully processed
            // into a durable, addressable outcome the caller can look up. A 4xx here would push callers into
            // treating a refusal as a transport error and discarding the run id and errors along with it.
            return Results.Ok(new WorkflowRunValidationResult(
                errors.Count == 0, correlationId, workflowRun.Id, errors, parameters));
        }).AllowAnonymous();

        group.MapPost("/workflows/{workflowId:guid}/run", async (
            Guid workflowId,
            WorkflowRunRequest? request,
            IWorkflowDefinitionStore store,
            IWorkflowRunStore runStore,
            IRankedWorkflowOrchestrator orchestrator,
            ICurrentUserService currentUserService,
            IWorkflowRunTracker runTracker,
            IServiceScopeFactory scopeFactory,
            ILoggerFactory loggerFactory,
            IConfigurationRepository configurationRepository,
            IAuthorizationService authorizationService,
            ILicenseQuotaGuard licenseQuotaGuard,
            HttpContext httpContext,
            CancellationToken cancellationToken) =>
        {
            // callerId is the interactive token-cache session key, and it arrives in the BODY here rather than on
            // the query string — too late for the pipeline-level resolver to have seen it. Stamp it now, before
            // the run (and everything it logs) is created, so this run shares the correlation id of the
            // validate-run/token-status/sign-in legs that preceded it. No-op when the caller supplied an explicit
            // X-Correlation-Id, or sent no callerId (a portal/scheduler run, which has no interactive session).
            WorkflowCorrelationResolver.ApplyDerived(httpContext, workflowId, request?.CallerId);

            var workflow = await store.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.NotFound();
            }

            // Which credential is this caller presenting? The portal sends the session cookie (and is subject to
            // the full RBAC below); a third-party standalone app sends neither cookie nor bearer token and is
            // instead gated on the workflow's own public-launch opt-in. Deciding this ONCE here keeps the two
            // paths from drifting apart, and makes the anonymous path's narrower checks explicit rather than
            // implied by a policy that silently never ran.
            var isAuthenticatedCaller = httpContext.User.Identity?.IsAuthenticated == true;

            if (isAuthenticatedCaller)
            {
                // Unchanged portal behaviour: the workflow-module action gate that used to sit on the route as
                // RequireAuthorization(HasPermission(workflow.run)). Moved in-handler (not weakened) so the route
                // can stay anonymous for the standalone caller below.
                var runPermission = PermissionTaxonomy.BuildPermissionCode(
                    PermissionGroupCode.Workflow, PermissionActionCode.Run);
                var runAuthorization = await authorizationService.AuthorizeAsync(
                    httpContext.User, AuthorizationPolicies.HasPermission(runPermission));
                if (!runAuthorization.Succeeded)
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }
            }
            else if (!workflow.IsPubliclyLaunchable)
            {
                // Same refusal shape and reasoning as /latest-launch-result: an un-opted-in workflow is reported
                // as absent rather than forbidden, so the workflow id alone can't be used to probe which ids
                // exist. The id is not a credential — it travels through URLs, configs and support tickets.
                return Results.NotFound();
            }

            // This is the Runtime plane's real run-trigger point (IRankedWorkflowOrchestrator.ExecuteAsync below,
            // both the sync and fire-and-forget-async branches) — checked once here, before either branch starts,
            // never mid-run. Blocks a truly expired license or an exhausted monthly processed-records cap; never
            // interrupts a run already in flight.
            await licenseQuotaGuard.EnsureCanStartNewRunAsync(cancellationToken);

            // Workflow-module gate (workflow.run, on the route below) is necessary but not sufficient —
            // independently, in addition, every distinct source vendor / destination type this specific
            // workflow's graph actually uses must have its own Execute permission too (originally seeded,
            // for Epic/Athenahealth/Cerner, specifically for "trigger a pipeline run against" this vendor —
            // see RbacSeedData — now enforced for real, and extended to every other node the same way).
            //
            // Same loops also re-validate each source/destination against the LICENSE's own allow-lists
            // (independent of the RBAC permission check above) — this is what catches a hospital/vendor/
            // destination type being dropped from the license on renewal, or a connection's BaseUrl edited
            // after creation, since LicenseEnforcementSaveChangesInterceptor only ever sees the row at its
            // own creation time, never again after that. Throws (LicenseRestrictionViolationException),
            // caught by the global exception handler and mapped to 403, exactly like EnsureCanStartNewRunAsync
            // above.
            foreach (var node in workflow.Nodes.Where(n => n.Category == WorkflowNodeCategory.Source))
            {
                if (!TryGetConfigurationGuid(node.ConfigurationJson, "sourceConnectionId", out var sourceConnectionId))
                {
                    continue;
                }

                var source = await configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
                if (source is null)
                {
                    continue;
                }

                // Per-vendor RBAC applies to a USER's permissions, so it is meaningful only for the
                // cookie-authenticated portal caller. For the anonymous standalone caller there is no principal to
                // check — its authorization is the IsPubliclyLaunchable opt-in above plus its callerId, the same
                // trust model /workflows/checkpoint/{token} uses (which likewise runs the license checks below
                // while having no user permissions to test).
                if (isAuthenticatedCaller
                    && !await ControllerAuthorizationExtensions.HasPermissionAsync(
                        authorizationService, httpContext.User, source.SourceSystemType, PermissionActionCode.Execute))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                // Deliberately OUTSIDE the isAuthenticatedCaller guard: the license allow-list is a property of
                // the deployment, not of the caller, so a standalone run must satisfy it too.
                await licenseQuotaGuard.EnsureSourceConnectionStillAllowedAsync(
                    source.SourceSystemType, source.BaseUrl, cancellationToken);
            }

            foreach (var node in workflow.Nodes.Where(n => n.Category == WorkflowNodeCategory.Destination))
            {
                if (!TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out var destinationId))
                {
                    continue;
                }

                var destination = await configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
                if (destination is null)
                {
                    continue;
                }

                // Same split as the source loop above: user-permission check for the portal caller only, license
                // allow-list for every caller.
                if (isAuthenticatedCaller
                    && !await ControllerAuthorizationExtensions.HasPermissionAsync(
                        authorizationService, httpContext.User, destination.DestinationType, PermissionActionCode.Execute))
                {
                    return Results.StatusCode(StatusCodes.Status403Forbidden);
                }

                await licenseQuotaGuard.EnsureDestinationTypeAllowedAsync(destination.DestinationType, cancellationToken);
            }

            // Reuse the ambient correlation id (same header/TraceIdentifier the API's global exception handler and
            // ErrorLogs use) so this run's ErrorLogs, audit trail, and outbound API Requests can all be found via
            // the same id — see the checkpoint endpoint below for the matching pattern.
            var runCorrelationId =
                request?.CorrelationId ?? currentUserService.CurrentUser.CorrelationId ?? Guid.NewGuid().ToString("N");

            // Continue the row validate-run already created for this attempt, rather than opening a second one, so
            // one user action is one Execution History entry spanning validation → sign-in → execution. Returns
            // null for every caller that never called validate-run (the portal's Run button, the scheduler,
            // webhooks), which therefore keeps creating its own run exactly as before.
            // Only the id is taken: the orchestrator builds its own WorkflowRun for that id and its up-front
            // "Running" placeholder is what moves the row out of Validated (SaveAsync replaces a non-terminal row
            // wholesale). Transitioning it here instead would attach a second tracked instance for the same key
            // and silently suppress that write on the synchronous path, which shares one DbContext.
            var validatedRun = await runStore.FindValidatedAsync(workflowId, runCorrelationId, cancellationToken);
            var workflowRunId = validatedRun?.Id ?? Guid.NewGuid();
            var context = new WorkflowExecutionContext(
                workflowRunId,
                runCorrelationId,
                triggeredBy: currentUserService.CurrentUser.AuditName,
                triggerType: "Manual",
                targetPatientId: request?.PatientId,
                patientSearchCriteria: request?.PatientSearchCriteria,
                callerId: request?.CallerId);

            if (request?.Async == true)
            {
                // Owned by the tracker from here on — MarkComplete disposes it once the run finishes either way,
                // and RequestCancellation (see the /cancel endpoint below) is the only other thing that touches it.
                var runCancellationSource = new CancellationTokenSource();
                runTracker.MarkRunning(workflowRunId, runCancellationSource);

                // Fire-and-forget on purpose: the caller gets the run id back now and polls for status, so this
                // must survive the HTTP request (and its scoped DbContext) ending. Resolve a fresh scope rather
                // than closing over the request-scoped orchestrator/store.
                _ = Task.Run(async () =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var scopedOrchestrator = scope.ServiceProvider.GetRequiredService<IRankedWorkflowOrchestrator>();
                    // No HttpContext survives into this detached Task.Run, so HttpContextCurrentUserService would
                    // otherwise stamp every AuditLog/AuthenticationLog/SecurityEvent this run produces with a null
                    // CorrelationId — set the ambient scope explicitly so it falls back to this run's real id instead.
                    var ambientActorContext = scope.ServiceProvider.GetRequiredService<IAmbientActorContext>();
                    using var actorScope = ambientActorContext.BeginCorrelatedScope("Background Workflow Run", context.CorrelationId);
                    try
                    {
                        await scopedOrchestrator.ExecuteAsync(workflow, context, runCancellationSource.Token);
                    }
                    catch (Exception exception)
                    {
                        // The orchestrator already persists the failed run with its error message and captures it
                        // via IGlobalExceptionManager (same CorrelationId) before rethrowing; this is just so a
                        // background failure isn't silently swallowed from an ops/logging perspective.
                        loggerFactory.CreateLogger("WorkflowEndpoints")
                            .LogError(exception, "Background run {WorkflowRunId} for workflow {WorkflowId} failed.", workflowRunId, workflowId);
                    }
                    finally
                    {
                        runTracker.MarkComplete(workflowRunId);
                    }
                });

                return Results.Accepted(
                    $"/api/v1/workflow-runs/{workflowRunId}/status",
                    new WorkflowRunStatusResponse(workflowRunId, "Running", context.CorrelationId));
            }

            // Tracked exactly like the async branch above, even though this call blocks the HTTP request.
            // Execution History shows "Cancel Run" for ANY run sitting at Running — it has no way to tell a
            // synchronous run from a background one, because that distinction isn't part of the run's status.
            // While only async runs were registered here, cancelling a foreground run looked up an id the
            // tracker had never seen, answered 409 "not currently active", and the run carried on to
            // completion: the button appeared to do nothing, which is precisely what QA reported.
            //
            // Linked to the request's own token so the existing behaviour is preserved — the caller
            // disconnecting still aborts the run — while /cancel now has a source it can signal.
            using var syncCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runTracker.MarkRunning(workflowRunId, syncCancellationSource);
            try
            {
                var result = await orchestrator.ExecuteAsync(workflow, context, syncCancellationSource.Token);
                return Results.Ok(result);
            }
            finally
            {
                // Drops the tracker's entry so a later cancel for this id correctly answers 409 rather than
                // signalling a disposed source. MarkComplete disposes the source it holds; this method's own
                // `using` then disposes the same instance, which is harmless (Dispose is idempotent).
                runTracker.MarkComplete(workflowRunId);
            }
        // Anonymous at the ROUTE level, authorized inside the handler instead — this endpoint serves two
        // callers with two different credentials. A portal run is cookie-authenticated and still requires
        // workflow.run plus per-vendor Execute (enforced at the top of the handler); a third-party standalone
        // run (Demo_TestApp) presents no cookie and is gated on the workflow's IsPubliclyLaunchable opt-in and
        // its unguessable callerId, exactly like /latest-launch-result and /workflows/checkpoint/{token}.
        // A blanket RequireAuthorization here would 401 that second caller before the handler ever ran.
        // [CsrfExempt] because the surviving credential is never the session cookie: see CsrfExemptAttribute.
        }).AllowAnonymous().WithMetadata(new CsrfExemptAttribute());

        // Lightweight poll target for an async /run — cheap enough to hit every second or two without pulling the
        // full node timeline. "Running" comes from IWorkflowRunTracker (the run hasn't reached a terminal state
        // and been persisted yet); once persisted, this reflects the same status /workflow-runs/{runId} would.
        group.MapGet("/workflow-runs/{runId:guid}/status", (
            Guid runId,
            IWorkflowRunTracker runTracker,
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            if (runTracker.IsRunning(runId))
            {
                return Task.FromResult(Results.Ok(new WorkflowRunStatusResponse(runId, "Running")));
            }

            return ResolveTerminalStatusAsync(runId, runStore, cancellationToken);

            static async Task<IResult> ResolveTerminalStatusAsync(
                Guid runId,
                IWorkflowRunStore runStore,
                CancellationToken cancellationToken)
            {
                var run = await runStore.GetAsync(runId, cancellationToken);
                return run is null
                    ? Results.NotFound()
                    : Results.Ok(new WorkflowRunStatusResponse(run.Id, run.Status.ToString(), run.CorrelationId));
            }
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.View)));

        // Requests a graceful stop of an in-flight async run (see the Async branch of /run above). The orchestrator
        // only checks for this between nodes (RankedWorkflowOrchestrator.RunNodesAsync) — the node already
        // executing when this is called is left to finish and keeps its normal terminal state, so cancelling never
        // leaves a node run half-written. A run already past this window (finished, or never started as async in
        // the first place) reports 409 rather than silently no-op'ing.
        group.MapPost("/workflow-runs/{runId:guid}/cancel", (
            Guid runId,
            IWorkflowRunTracker runTracker) =>
        {
            return runTracker.RequestCancellation(runId)
                ? Results.Accepted(value: new WorkflowRunStatusResponse(runId, "CancellationRequested"))
                : Results.Conflict(new { message = "This run is not currently active and cannot be cancelled." });
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Run)));

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
            ILicenseQuotaGuard licenseQuotaGuard,
            IConfigurationRepository configurationRepository,
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

            // Same Runtime-plane run-trigger gate as /workflows/{workflowId}/run above — this anonymous URL
            // starts a brand-new run (a fresh workflowRunId below) just as much as that endpoint does.
            await licenseQuotaGuard.EnsureCanStartNewRunAsync(cancellationToken);

            // Same source/destination license allow-list re-check as /workflows/{workflowId}/run above (see
            // its matching comment for why this can't just rely on create-time enforcement). Checked against
            // every source/destination node in the workflow, same as that endpoint, not narrowed to this
            // checkpoint's ancestor closure — simpler, and never under-checks.
            foreach (var sourceNode in workflow.Nodes.Where(n => n.Category == WorkflowNodeCategory.Source))
            {
                if (!TryGetConfigurationGuid(sourceNode.ConfigurationJson, "sourceConnectionId", out var sourceConnectionId))
                {
                    continue;
                }

                var source = await configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
                if (source is not null)
                {
                    await licenseQuotaGuard.EnsureSourceConnectionStillAllowedAsync(
                        source.SourceSystemType, source.BaseUrl, cancellationToken);
                }
            }

            foreach (var destinationNode in workflow.Nodes.Where(n => n.Category == WorkflowNodeCategory.Destination))
            {
                if (!TryGetConfigurationGuid(destinationNode.ConfigurationJson, "destinationId", out var destinationId))
                {
                    continue;
                }

                var destination = await configurationRepository.GetDestinationAsync(destinationId, cancellationToken);
                if (destination is not null)
                {
                    await licenseQuotaGuard.EnsureDestinationTypeAllowedAsync(destination.DestinationType, cancellationToken);
                }
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

            // Metadata only: the node's captured output is no longer retained (it was whole Epic FHIR resources,
            // encrypted at rest — still PHI), so this reports WHAT the checkpointed node produced, not the content.
            return payload is null
                ? Results.Ok(new { result = (JsonNode?)null, contract = (string?)null })
                : Results.Ok(new
                {
                    result = (JsonNode?)null,
                    contract = payload.Contract,
                    itemCount = payload.ItemCount,
                    resourceCounts = string.IsNullOrWhiteSpace(payload.ResourceTypeCountsJson)
                        ? null
                        : JsonNode.Parse(payload.ResourceTypeCountsJson),
                });
        });

        // Persisted run history (Scenario A): node-by-node execution timeline for the builder UI.
        group.MapGet("/workflows/{workflowId:guid}/runs", async (
            Guid workflowId,
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
            Results.Ok(await runStore.ListByDefinitionAsync(workflowId, cancellationToken)))
        .RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.View)));

        group.MapGet("/workflow-runs/{runId:guid}", async (
            Guid runId,
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            var run = await runStore.GetAsync(runId, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.View)));

        // ── Execution History (global, across every workflow) ───────────────────────
        // Backs the portal's Execution History screen for the Runtime Plane — the path actually exercised by
        // "Run" and interactive (EHR launch / standalone) launches, as opposed to the Configured Pipeline's
        // separate route-execution history under /api/v1/pipeline-runs/route-executions.
        group.MapGet("/workflow-runs", async (
            // Set when the Workflows list's "Execution History" row action deep-links here — narrows to one
            // workflow's runs before anything else (including the Source filter's own option list below), so
            // browsing that filter afterward only ever offers sources relevant to THIS workflow, not every
            // source across every workflow.
            Guid? workflowId,
            string? status,
            string? source,
            // Multi-select Source filter (repeated ?sources=), matching the Workflows list. `source` above stays for
            // the single-value callers that link straight here (the Dashboard's widgets); both are honoured. Named
            // sourceFilters locally because the handler body already binds `sources` to the source CONNECTIONS.
            [Microsoft.AspNetCore.Mvc.FromQuery(Name = "sources")] string[]? sourceFilters,
            string? triggeredBy,
            string? search,
            int? page,
            int? pageSize,
            string? sortColumn,
            string? sortDirection,
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
                    run.WorkflowDefinitionVersion,
                    run.CorrelationId,
                    run.ErrorReferenceId,
                    run.BulkRequestId));
            }

            if (workflowId is { } wfId)
            {
                items = items.Where(x => x.WorkflowDefinitionId == wfId).ToList();
            }

            // Computed before any OTHER filter is applied, so the Source filter's options are "vendors that have
            // actually run" (within whatever workflowId already narrowed it to above) rather than every EHR the
            // platform supports — and don't vanish as the user narrows the list further.
            var availableSourceSystemTypes = items
                .Select(x => x.SourceSystemType)
                .OfType<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (!string.IsNullOrWhiteSpace(status))
            {
                // AwaitingBulkExport is non-terminal — a node deferred to an async $export job while the run is
                // still in flight — so the portal presents it as "Running" and /workflow-runs/stats already counts
                // it in the Running tile. Filtering must agree: an exact-match-only filter here is what made the
                // Dashboard's Running count not match what clicking that tile actually listed.
                var matchesAwaitingAsRunning = string.Equals(status, "Running", StringComparison.OrdinalIgnoreCase);
                items = items.Where(x =>
                    string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase)
                    || (matchesAwaitingAsRunning
                        && string.Equals(x.Status, "AwaitingBulkExport", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(source))
            {
                items = items.Where(x =>
                    string.Equals(x.SourceName, source, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x.SourceSystemType, source, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            if (sourceFilters is { Length: > 0 })
            {
                var selectedSources = new HashSet<string>(sourceFilters, StringComparer.OrdinalIgnoreCase);
                items = items.Where(x =>
                    (x.SourceName is not null && selectedSources.Contains(x.SourceName))
                    || (x.SourceSystemType is not null && selectedSources.Contains(x.SourceSystemType))).ToList();
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
            var paged = SortRuns(items, sortColumn, sortDirection)
                .Skip((effectivePage - 1) * effectivePageSize)
                .Take(effectivePageSize)
                .ToList();

            return Results.Ok(new WorkflowRunHistoryPageDto(
                paged, totalCount, effectivePage, effectivePageSize, availableSourceSystemTypes));
        // Menu-level gate: backs both the Dashboard's "Recent Workflows" widget and the Execution History
        // page — both reuse workflow.view rather than a dedicated permission (see sidebar/route changes).
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.View)));

        // All-time run count per status, across every workflow — unlike the paged /workflow-runs list above
        // (capped to the 500 most recent via ListRecentAsync), this queries the full WorkflowRuns table
        // directly so the Dashboard's stat tiles reflect a true global count. Backs the Dashboard screen.
        group.MapGet("/workflow-runs/stats", async (
            IWorkflowRunStore runStore,
            CancellationToken cancellationToken) =>
        {
            var counts = await runStore.GetStatusCountsAsync(cancellationToken);

            // AwaitingBulkExport is non-terminal — a node deferred to an async $export job and the run is still
            // in flight pending BulkExportPollWorker's resume — so it belongs in "Running", not invisible in no
            // tile at all. PartialSuccess gets its OWN dashboard tile (not folded into Succeeded) so a tile's
            // count always matches exactly what clicking through to Execution History filtered by that same
            // status shows — folding it into Succeeded here while the list only matches on the exact status
            // string was the original source of the two screens looking inconsistent.
            return Results.Ok(new WorkflowRunStatusCountsDto(
                counts[WorkflowRunStatus.Pending],
                counts[WorkflowRunStatus.Running] + counts[WorkflowRunStatus.AwaitingBulkExport],
                counts[WorkflowRunStatus.Succeeded],
                counts[WorkflowRunStatus.Failed],
                counts[WorkflowRunStatus.Cancelled],
                counts[WorkflowRunStatus.PartialSuccess]));
        // Menu-level gate: backs the Dashboard's stat tiles — reuses workflow.view, same as /workflow-runs above.
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.View)));

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
                run.WorkflowDefinitionVersion,
                run.CorrelationId,
                run.ErrorReferenceId,
                run.BulkRequestId));
        }).RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Live read of this run's FHIR Bulk Data $export job, proxied from the source server — backs the Execution
        // History row's "Bulk Data Status Request" popup. Proxied rather than fetched from the browser because the
        // status URL requires the source's bearer token, which must never leave the server; and read live rather
        // than served from the last poll tick so an operator watching a long export sees current progress.
        //
        // Gated on workflow.view, not UnifiedAdmin like /summary above: anyone who can see the run in the list
        // should be able to see WHY it is still running.
        group.MapGet("/workflow-runs/{runId:guid}/bulk-export-status", async (
            Guid runId,
            IWorkflowRunStore runStore,
            IBulkExportJobRepository bulkExportJobRepository,
            IFhirBulkExportClient bulkExportClient,
            ISourceConnectionRuntimeResolver sourceResolver,
            CancellationToken cancellationToken) =>
        {
            var run = await runStore.GetAsync(runId, cancellationToken);
            if (run is null)
            {
                return Results.NotFound();
            }

            var job = await bulkExportJobRepository.GetLatestByWorkflowRunAsync(runId, cancellationToken);
            if (job is null || string.IsNullOrWhiteSpace(job.StatusUrl))
            {
                // No export job, or one kicked off but not yet assigned a status URL — nothing to look up yet.
                // 404 rather than an empty body, so the portal can distinguish "no bulk export here" from "here is
                // an export with nothing in it".
                return Results.NotFound();
            }

            // The resource types this node actually asked for. While the job runs this is the ONLY source of a
            // per-type list — a Bulk Data server answers an in-flight job with 202 and no body — so without it the
            // popup's table would be empty for the whole duration of the export, which is exactly when it is opened.
            var requestedResourceTypes = ParseRequestedResourceTypes(job.RequestedResourceTypesJson);

            var source = await sourceResolver.ResolveAsync(
                job.SourceConnectionId, searchParameters: null, targetPatientId: null, cancellationToken);
            if (source is null)
            {
                // The source connection was deleted out from under an in-flight job. Report what is known locally
                // rather than failing outright — the id and timings are still useful.
                return Results.Ok(new BulkExportStatusDto(
                    run.BulkRequestId ?? BulkRequestIds.FromStatusUrl(job.StatusUrl),
                    job.Status,
                    Progress: null,
                    job.KickedOffOnUtc,
                    job.NextPollNotBeforeUtc,
                    job.PollAttemptCount,
                    TransactionTime: null,
                    Request: null,
                    RequiresAccessToken: null,
                    BuildPendingResourceTypes(requestedResourceTypes),
                    Errors: [],
                    RetryAfterSeconds: null,
                    ErrorMessage: "The source connection for this export no longer exists, so its live status "
                        + "cannot be read."));
            }

            var snapshot = await bulkExportClient.GetStatusAsync(job.StatusUrl, source, cancellationToken);

            return Results.Ok(new BulkExportStatusDto(
                run.BulkRequestId ?? BulkRequestIds.FromStatusUrl(job.StatusUrl),
                snapshot.Status.ToString(),
                snapshot.Progress,
                job.KickedOffOnUtc,
                job.NextPollNotBeforeUtc,
                job.PollAttemptCount,
                snapshot.TransactionTime,
                snapshot.Request,
                snapshot.RequiresAccessToken,
                BuildResourceTypeStatuses(snapshot, requestedResourceTypes),
                BuildManifestErrors(snapshot),
                snapshot.RetryAfter is { } retryAfter ? (int)retryAfter.TotalSeconds : null,
                snapshot.ErrorMessage));
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.View)));

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

        // Same drill-down, but always one row per node that actually started — success, failure, or
        // cancellation — instead of only ever showing nodes that wrote a success-path output payload. Fixes
        // failed nodes being silently absent from the Execution History screen.
        group.MapGet("/workflow-runs/{runId:guid}/node-runs", async (
            Guid runId,
            int? page,
            int? pageSize,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetNodeRunHistoryPagedAsync(
                runId,
                page is > 0 ? page.Value : 1,
                pageSize is > 0 ? pageSize.Value : 25,
                cancellationToken);

            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Split out of the list above so expanding a node's row only pays the decryption cost for that one
        // node's payload — not every node in the page (a source node's payload can hold thousands of resources).
        group.MapGet("/workflow-runs/{runId:guid}/node-runs/{nodeRunId:guid}/payload", async (
            Guid runId,
            Guid nodeRunId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetNodeRunPayloadAsync(runId, nodeRunId, cancellationToken);
            return result is null ? Results.NotFound() : Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Field-level lineage: one chain per (resource, destination field), each carrying the full
        // source -> node -> node -> destination hop chain the transform-rule engine produced for it. The
        // filter params back the portal's Group-by-Field/Patient/Node toggle and free-text search — all the
        // same query, just filtered differently (see FieldLineageFilter's remarks).
        group.MapGet("/workflow-runs/{runId:guid}/field-lineage", async (
            Guid runId,
            int? page,
            int? pageSize,
            string? resourceType,
            string? destinationField,
            string? resourceId,
            string? nodeType,
            string? search,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var filter = new FieldLineageFilter(resourceType, destinationField, resourceId, nodeType, search);
            var result = await recorder.GetFieldLineagePagedAsync(
                runId,
                page is > 0 ? page.Value : 1,
                pageSize is > 0 ? pageSize.Value : 25,
                filter,
                cancellationToken);

            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Run-wide field-lineage totals — backs the Lineage tab's stat strip (resources processed, fields
        // transformed, transformation nodes executed, success rate).
        group.MapGet("/workflow-runs/{runId:guid}/lineage/summary", async (
            Guid runId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetLineageSummaryAsync(runId, cancellationToken);
            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // Every resource type touched by this run's field lineage, with the destination fields under it and
        // how many distinct resources hit each one — backs the Lineage tab's resource-tree sidebar.
        group.MapGet("/workflow-runs/{runId:guid}/lineage/resource-tree", async (
            Guid runId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetLineageResourceTreeAsync(runId, cancellationToken);
            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // What each node actually applied — field mappings and transformation rules, per node. Execution
        // History's node list showed every node the same per-resource-type counts, which told you nothing
        // about a mapping node's real work; this backs the per-node summary lines there.
        group.MapGet("/workflow-runs/{runId:guid}/lineage/node-breakdown", async (
            Guid runId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetNodeLineageBreakdownAsync(runId, cancellationToken);
            return Results.Ok(result.Values);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // The transformation rules CONFIGURED on this run's workflow, grouped by resource type — what the
        // workflow is set up to do, as opposed to what this run happened to execute. Backs the transform
        // node's per-resource-type rule list in Execution History.
        group.MapGet("/workflow-runs/{runId:guid}/configured-rules", async (
            Guid runId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetConfiguredResourceTypeRulesAsync(runId, cancellationToken);
            return Results.Ok(result);
        })
        .RequireAuthorization(AuthorizationPolicies.UnifiedAdmin);

        // The DE-IDENTIFICATION rules configured for this run, grouped by resource type — same shape as
        // /configured-rules, read from the de-identification profile the run resolves to.
        group.MapGet("/workflow-runs/{runId:guid}/configured-deid-rules", async (
            Guid runId,
            IWorkflowNodeResourceHistoryRecorder recorder,
            CancellationToken cancellationToken) =>
        {
            var result = await recorder.GetConfiguredDeIdentificationRulesAsync(runId, cancellationToken);
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
        // Toggling IsEnabled modifies the workflow definition — workflow.edit, same as any other change made
        // to an existing workflow.
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Edit)));

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
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Edit)));

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
        // Workflow-module gate: can this role delete a workflow at all.
        }).RequireAuthorization(AuthorizationPolicies.HasPermission(
            PermissionTaxonomy.BuildPermissionCode(PermissionGroupCode.Workflow, PermissionActionCode.Delete)));

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

        var rootTableIdentifier = spec.DestinationObject.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [var name, ..]
            ? name
            : spec.DestinationObject;

        var root = schema.Tables.FirstOrDefault(t =>
            string.Equals(t.FullName, rootTableIdentifier, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.TableName, rootTableIdentifier, StringComparison.OrdinalIgnoreCase));

        // The configured root table isn't one this destination's live schema actually has — a different, more
        // specific failure than a bad column, and one the writer already reports clearly at run time; nothing
        // further to check here since there are no real columns to validate any field's name against.
        if (root is null)
        {
            return null;
        }

        // A field carrying its own DestinationObject (a SeparateDestination child-table field, e.g. Patient.name
        // fanned out into dbo.PatientName) must be checked against ITS OWN table's real columns, not the
        // resource's root table — a column that exists on the child table but not the root (or vice versa) would
        // otherwise report a false failure. Resolved lazily and cached since several fields typically share the
        // same child table.
        var tablesByIdentifier = new Dictionary<string, DestinationTableSchemaDto?>(StringComparer.OrdinalIgnoreCase)
        {
            [rootTableIdentifier] = root,
        };

        foreach (var field in spec.Fields)
        {
            var tableIdentifier = string.IsNullOrWhiteSpace(field.DestinationObject)
                ? rootTableIdentifier
                : field.DestinationObject;

            if (!tablesByIdentifier.TryGetValue(tableIdentifier, out var table))
            {
                table = schema.Tables.FirstOrDefault(t =>
                    string.Equals(t.FullName, tableIdentifier, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.TableName, tableIdentifier, StringComparison.OrdinalIgnoreCase));
                tablesByIdentifier[tableIdentifier] = table;
            }

            // Same leniency as the root-table-not-found case above — a child table this destination's live
            // schema doesn't (yet) have is a different failure the writer reports clearly at run time.
            if (table is null)
            {
                continue;
            }

            var realColumns = new HashSet<string>(table.Columns.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
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

    // ── /workflows/{id}/copy entity cloning ─────────────────────────────────────────────────────────────
    // Deep-clones the entities a workflow copy must NOT keep sharing with its original — see the copy endpoint's
    // own doc comment. Each helper is idempotent per (originalId, the passed-in map): a second call for the same
    // originalId returns the already-cloned id instead of creating a duplicate, since the same SourceConnection/
    // DestinationConfiguration/MappingProfile can legitimately be referenced by more than one node.

    private static async Task<(Guid Id, ApplicationType? ApplicationType)> CloneSourceConnectionAsync(
        Guid originalId,
        Dictionary<Guid, (Guid Id, ApplicationType? ApplicationType)> clonedIds,
        IConfigurationRepository configurationRepository,
        IConfigurationService configurationService,
        ISecretProvider secretProvider,
        ISecretWriter secretWriter,
        CancellationToken cancellationToken)
    {
        if (clonedIds.TryGetValue(originalId, out var alreadyCloned))
        {
            return alreadyCloned;
        }

        var original = await configurationRepository.GetSourceConnectionAsync(originalId, cancellationToken);
        if (original is null)
        {
            // Nothing to clone (a stale/dangling id already orphaned before this copy) — leave the node pointed
            // at whatever it already had rather than failing the whole copy over an unrelated, pre-existing gap.
            var fallback = (originalId, (ApplicationType?)null);
            clonedIds[originalId] = fallback;
            return fallback;
        }

        var dto = ConfigurationMapper.ToDto(original);
        var authentication = await CloneAuthenticationAsync(dto.Authentication, secretProvider, secretWriter, cancellationToken);

        // SourceConnection.Name must be unique (see IConfigurationRepository.ExistsWithNameAsync) — a short random
        // suffix guarantees that regardless of how many times the same workflow (or the same source) gets copied.
        var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
        var clonedName = $"{dto.Name} (copy {uniqueSuffix})";
        var createRequest = new CreateSourceConnectionRequest(
            clonedName,
            dto.SourceSystemType,
            dto.BaseUrl,
            authentication,
            dto.ApplicationType,
            dto.Interactive,
            // Resets the incremental-sync cursor — the clone has never actually run, so LastSuccessfulSyncUtc
            // carried over from the original would make its first real run think resources up to that point
            // were already fetched by THIS connection, silently skipping them.
            dto.Retrieval is { } retrieval ? retrieval with { LastSuccessfulSyncUtcByResourceType = null } : null);

        var cloned = await configurationService.AddSourceConnectionAsync(createRequest, cancellationToken);
        var result = (cloned.Id, cloned.ApplicationType);
        clonedIds[originalId] = result;
        return result;
    }

    /// <summary>Mirrors the portal's APPLICATION_TYPE_TO_AUDIENCE (wizard.service.ts) — the machine slug the
    /// EHR-vendor source form caches on its own node under the "Epic audience" key, used to pre-populate that
    /// form's dropdown when the node is reopened, rather than ever re-deriving it from the live SourceConnection.
    /// Resolved from each type's own strategy descriptor (PortalAudienceSlug) rather than a switch here, so a new
    /// application type is a new strategy plus a registration and never an edit to this endpoint — see
    /// ApplicationTypeDispatchTests.
    /// Falls back to 'provider-ehr-launch' for a null ApplicationType (a legacy connection created before the enum
    /// existed) and for an unregistered one, matching the portal's own null-safe default.</summary>
    private static string ApplicationTypeToEpicAudienceSlug(
        ISourceApplicationStrategyRegistry applicationStrategies,
        ApplicationType? applicationType)
    {
        const string PortalDefaultAudienceSlug = "provider-ehr-launch";

        if (applicationType is not { } type)
        {
            return PortalDefaultAudienceSlug;
        }

        return applicationStrategies.TryResolve(type, out var strategy)
            ? strategy.Describe().PortalAudienceSlug
            : PortalDefaultAudienceSlug;
    }

    private static async Task<(Guid Id, string KeyVaultName, string SecretName)> CloneDestinationConfigurationAsync(
        Guid originalId,
        Dictionary<Guid, (Guid Id, string KeyVaultName, string SecretName)> clonedDestinations,
        IConfigurationRepository configurationRepository,
        IConfigurationService configurationService,
        ISecretProvider secretProvider,
        ISecretWriter secretWriter,
        CancellationToken cancellationToken)
    {
        if (clonedDestinations.TryGetValue(originalId, out var already))
        {
            return already;
        }

        var original = await configurationRepository.GetDestinationAsync(originalId, cancellationToken);
        if (original is null)
        {
            var fallback = (originalId, string.Empty, string.Empty);
            clonedDestinations[originalId] = fallback;
            return fallback;
        }

        var dto = ConfigurationMapper.ToDto(original);
        var secretReference = await CloneSecretAsync(dto.KeyVaultName, dto.SecretName, secretProvider, secretWriter, cancellationToken)
            ?? new SecretReference(dto.KeyVaultName, dto.SecretName);

        var createRequest = new CreateDestinationConfigurationRequest(
            dto.Name,
            dto.DestinationType,
            secretReference.KeyVaultName,
            secretReference.SecretName,
            dto.Target,
            InlineSecret: null,
            ConnectionMetadataJson: dto.ConnectionMetadataJson);

        var cloned = await configurationService.AddDestinationConfigurationAsync(createRequest, cancellationToken);
        var result = (cloned.Id, cloned.KeyVaultName, cloned.SecretName);
        clonedDestinations[originalId] = result;
        return result;
    }

    private static async Task<Guid> CloneMappingProfileAsync(
        Guid originalId,
        Dictionary<Guid, Guid> clonedMappingProfileIds,
        Dictionary<Guid, (Guid Id, ApplicationType? ApplicationType)> clonedSourceConnectionIds,
        Dictionary<Guid, (Guid Id, string KeyVaultName, string SecretName)> clonedDestinations,
        IConfigurationRepository configurationRepository,
        IConfigurationService configurationService,
        CancellationToken cancellationToken)
    {
        if (clonedMappingProfileIds.TryGetValue(originalId, out var already))
        {
            return already;
        }

        var original = await configurationRepository.GetMappingProfileAsync(originalId, cancellationToken);
        if (original is null)
        {
            clonedMappingProfileIds[originalId] = originalId;
            return originalId;
        }

        var dto = ConfigurationMapper.ToDto(original);
        var newSourceConnectionId = clonedSourceConnectionIds.TryGetValue(dto.SourceConnectionId, out var clonedSourceForMapping)
            ? clonedSourceForMapping.Id
            : dto.SourceConnectionId;
        var newDestinationId = clonedDestinations.TryGetValue(dto.DestinationId, out var destInfo)
            ? destInfo.Id
            : dto.DestinationId;

        var createRequest = new CreateMappingProfileRequest(
            dto.Name,
            dto.ResourceType,
            newSourceConnectionId,
            newDestinationId,
            dto.DestinationObject,
            dto.Fields);
        // SourceConfigurationId deliberately omitted (null) — AddMappingProfileAsync's own
        // ResolveSourceConfigurationForCreateAsync auto-provisions a fresh, independent SourceConfiguration for
        // the cloned SourceConnectionId, rather than this clone reusing the original's.
        var cloned = await configurationService.AddMappingProfileAsync(createRequest, cancellationToken);

        // CreateMappingProfileRequest has no MappingJson slot — a profile authored via the richer Mapping Config
        // Import wizard (MappingJson set) needs that raw JSON carried over directly onto the entity, or the
        // clone would silently downgrade to only the flattened Fields projection AddMappingProfileAsync builds.
        if (!string.IsNullOrWhiteSpace(dto.MappingJson))
        {
            var clonedEntity = await configurationRepository.GetMappingProfileAsync(cloned.Id, cancellationToken);
            if (clonedEntity is not null)
            {
                clonedEntity.SetMappingJson(dto.MappingJson);
                await configurationRepository.UpdateMappingProfileAsync(clonedEntity, cancellationToken);
            }
        }

        clonedMappingProfileIds[originalId] = cloned.Id;
        return cloned.Id;
    }

    /// <summary>Re-provisions ClientSecret/PrivateKey under brand-new secret names so the clone's credentials are
    /// never the same stored secret as the original's — rotating or deleting one must never affect the other.</summary>
    private static async Task<SourceAuthenticationDto> CloneAuthenticationAsync(
        SourceAuthenticationDto original,
        ISecretProvider secretProvider,
        ISecretWriter secretWriter,
        CancellationToken cancellationToken)
    {
        var clientSecret = await CloneSecretAsync(
            original.ClientSecretKeyVaultName, original.ClientSecretName, secretProvider, secretWriter, cancellationToken);
        var privateKey = await CloneSecretAsync(
            original.PrivateKeyKeyVaultName, original.PrivateKeySecretName, secretProvider, secretWriter, cancellationToken);

        return original with
        {
            ClientSecretKeyVaultName = clientSecret?.KeyVaultName,
            ClientSecretName = clientSecret?.SecretName,
            PrivateKeyKeyVaultName = privateKey?.KeyVaultName,
            PrivateKeySecretName = privateKey?.SecretName,
        };
    }

    /// <summary>Reads the secret value at (keyVaultName, secretName) and re-provisions it under a brand-new secret
    /// name — never the original's — so the copy's credential is fully independent (see ISecretWriter's own doc
    /// comment: it always lands in the app's DB-provisioned secret store, functionally equivalent to the original
    /// regardless of whether the original itself came from real Azure Key Vault or that same store). Null when
    /// there's no secret reference to clone in the first place (an optional auth field the connection never set),
    /// OR when a reference exists but nothing was ever actually written there — e.g. a managed-identity-authenticated
    /// destination/source (Azure FHIR Service, Blob Storage) always carries non-blank KeyVaultName/SecretName
    /// (required by validation) but never calls ISecretWriter.WriteSecretAsync for that mode, since there's no
    /// secret value to store. Without this second check, duplicating such a connection throws
    /// SecretNotConfiguredException instead of just producing an equally-secret-less clone.</summary>
    private static async Task<SecretReference?> CloneSecretAsync(
        string? keyVaultName,
        string? secretName,
        ISecretProvider secretProvider,
        ISecretWriter secretWriter,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(keyVaultName) || string.IsNullOrWhiteSpace(secretName))
        {
            return null;
        }

        string value;
        try
        {
            value = await secretProvider.GetSecretAsync(new SecretReference(keyVaultName, secretName), cancellationToken);
        }
        catch (SecretNotConfiguredException)
        {
            return null;
        }

        var newReference = new SecretReference(keyVaultName, $"{secretName}-copy-{Guid.NewGuid():N}");
        await secretWriter.WriteSecretAsync(newReference, value, cancellationToken);
        return newReference;
    }

    /// <summary>
    /// Mutates a node's configuration and re-serializes it. Uses <see cref="TryParseConfiguration"/> (the ROOT),
    /// never <see cref="TryParseConfigurationSettings"/>: whatever this is handed becomes the ENTIRE node
    /// configuration, so resolving an envelope here would save the inner <c>config</c> object as the whole thing
    /// and drop the envelope and every sibling key with it.
    /// </summary>
    private static WorkflowNodeRequest WithConfiguration(WorkflowNodeRequest node, Action<JsonObject> mutate)
    {
        var config = TryParseConfiguration(node.ConfigurationJson) ?? new JsonObject();
        mutate(config);
        return node with { ConfigurationJson = config.ToJsonString() };
    }

    private static string? ReadConfigString(WorkflowNodeRequest node, string key) =>
        TryParseConfigurationSettings(node.ConfigurationJson)?[key]?.ToString();

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
            && TryParseConfigurationSettings(node.ConfigurationJson) is { } config
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

    /// <summary>Mirrors the portal's audienceLabel() so 'audience' sorts the same friendly grouping the column
    /// displays, not the raw ApplicationType enum name.</summary>
    private static string AudienceSortLabel(string? applicationType) => applicationType switch
    {
        "EhrLaunch"  => "EHR Launch (Provider)",
        "Standalone" => "Provider Standalone",
        "Patient"    => "Patient Standalone",
        "Backend"    => "Backend Service",
        _            => "—",
    };

    private static IEnumerable<WorkflowSummaryDto> SortSummaries(
        IEnumerable<WorkflowSummaryDto> summaries, string? sortColumn, string? sortDirection)
    {
        var descending = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<WorkflowSummaryDto> Order<TKey>(Func<WorkflowSummaryDto, TKey> keySelector) =>
            descending
                ? summaries.OrderByDescending(keySelector)
                : summaries.OrderBy(keySelector);

        return sortColumn switch
        {
            "source"   => Order(summary => (summary.SourceSystemType ?? string.Empty).ToLowerInvariant()),
            "audience" => Order(summary => AudienceSortLabel(summary.ApplicationType).ToLowerInvariant()),
            "status"   => Order(summary => summary.Status),
            "lastRun"  => Order(summary => summary.LastRunAt?.UtcTicks ?? -1),
            "actionOn" => Order(summary => (summary.ModifiedOnUtc ?? summary.CreatedOnUtc)?.Ticks ?? -1),
            _          => Order(summary => summary.Name.ToLowerInvariant()),
        };
    }

    /// <summary>The resource types a deferred bulk-export node asked for, as persisted on the job row. Returns
    /// empty (never throws) for null/blank/malformed JSON — the popup degrades to whatever the manifest provides
    /// rather than failing on a bad row.</summary>
    private static IReadOnlyList<string> ParseRequestedResourceTypes(string? requestedResourceTypesJson)
    {
        if (string.IsNullOrWhiteSpace(requestedResourceTypesJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(requestedResourceTypesJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<BulkExportResourceTypeStatusDto> BuildPendingResourceTypes(
        IReadOnlyList<string> requestedResourceTypes)
        => requestedResourceTypes
            .Select(resourceType => new BulkExportResourceTypeStatusDto(resourceType, FileCount: null, State: "Pending"))
            .ToList();

    /// <summary>
    /// Projects a status read onto ONE per-type list the portal renders identically in both states.
    ///
    /// <para>In flight, the server has returned 202 with no manifest, so every requested type is reported
    /// <c>Pending</c> with no count — this is what keeps the popup's table populated while the export runs, which
    /// is when an operator actually opens it.</para>
    ///
    /// <para>On completion, the manifest's <c>output</c> entries are grouped by type into FILE counts (never record
    /// counts — see <see cref="BulkExportResourceTypeStatusDto"/>). A requested type the manifest never mentions is
    /// still emitted, as <c>Pending</c> with 0, so a type the server quietly dropped stays visible instead of
    /// vanishing from the list.</para>
    ///
    /// <para>The manifest's signed <c>url</c> values are deliberately discarded here: they are directly downloadable
    /// NDJSON of bulk PHI, and this projection is what keeps them off the wire.</para>
    /// </summary>
    private static IReadOnlyList<BulkExportResourceTypeStatusDto> BuildResourceTypeStatuses(
        BulkExportStatusSnapshot snapshot,
        IReadOnlyList<string> requestedResourceTypes)
    {
        if (snapshot.Files is not { Count: > 0 })
        {
            return BuildPendingResourceTypes(requestedResourceTypes);
        }

        var fileCountsByType = snapshot.Files
            .GroupBy(file => file.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var statuses = fileCountsByType
            .Select(entry => new BulkExportResourceTypeStatusDto(entry.Key, entry.Value, State: "Ready"))
            .ToList();

        statuses.AddRange(requestedResourceTypes
            .Where(resourceType => !fileCountsByType.ContainsKey(resourceType))
            .Select(resourceType => new BulkExportResourceTypeStatusDto(resourceType, FileCount: 0, State: "Pending")));

        return statuses
            .OrderBy(status => status.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The manifest's <c>error</c> array surfaced as plain text. Only the file's resource-type label is
    /// available without downloading each OperationOutcome, which this deliberately does not do — the popup is a
    /// cheap status read, and those files are fetched (and their issues parsed) by the poller's own completion path.</summary>
    private static IReadOnlyList<string> BuildManifestErrors(BulkExportStatusSnapshot snapshot)
    {
        var errors = new List<string>();

        if (!string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            errors.Add(snapshot.ErrorMessage);
        }

        if (snapshot.ErrorFiles is { Count: > 0 })
        {
            errors.AddRange(snapshot.ErrorFiles
                .GroupBy(file => file.ResourceType, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.Count() == 1
                    ? $"The source server reported an issue for {group.Key}."
                    : $"The source server reported {group.Count()} issues for {group.Key}."));
        }

        return errors;
    }

    // Defaults to newest-first by start time — matches this endpoint's pre-sorting behavior before
    // sortColumn/sortDirection existed, so an unsorted request (the initial page load) looks unchanged.
    private static IEnumerable<WorkflowRunHistoryDto> SortRuns(
        IEnumerable<WorkflowRunHistoryDto> runs, string? sortColumn, string? sortDirection)
    {
        var descending = !string.Equals(sortDirection, "asc", StringComparison.OrdinalIgnoreCase);

        IOrderedEnumerable<WorkflowRunHistoryDto> Order<TKey>(Func<WorkflowRunHistoryDto, TKey> keySelector) =>
            descending
                ? runs.OrderByDescending(keySelector)
                : runs.OrderBy(keySelector);

        return sortColumn switch
        {
            "pipeline"    => Order(run => run.PipelineName.ToLowerInvariant()),
            "source"      => Order(run => (run.SourceName ?? run.SourceSystemType ?? string.Empty).ToLowerInvariant()),
            "status"      => Order(run => run.Status),
            "duration"    => Order(run => run.DurationMs ?? -1),
            "triggeredBy" => Order(run => (run.TriggeredBy ?? run.TriggerType ?? string.Empty).ToLowerInvariant()),
            _             => Order(run => run.StartedAt),
        };
    }

    // The set of permission-group wire-prefixes that represent a workflow "node" (a source vendor or
    // destination type usable inside a workflow) rather than the Workflow module itself — same
    // enum-crossing composition as WorkflowModuleAccessAuthorizationHandler.NodeGroupPrefixes (kept as a
    // private copy there; duplicated here rather than made public, matching this file's existing
    // tolerance for small duplication over shared-helper indirection — see the node-removal check above).
    private static readonly Lazy<HashSet<string>> NodeGroupPrefixes = new(() =>
        new HashSet<string>(
            SourceSystemPermissionGroups.AllGroupsFor(typeof(SourceSystemType))
                .Concat(SourceSystemPermissionGroups.AllGroupsFor(typeof(DestinationType)))
                .Select(group => group.ToString()),
            StringComparer.OrdinalIgnoreCase));

    // Row-level workflow visibility (see /workflows/summary and the single-workflow GET below).
    //
    // The Role Permissions screen's dependency engine (permission-matrix-dependencies.ts) auto-includes
    // workflow.view — and, depending on which action was checked, workflow.create/edit/delete/run too —
    // as an implied parent of ANY single vendor permission (epic.view, sqlserver.edit, ...), by design,
    // purely so the screen never shows an internally-inconsistent saved state. Its own header comment is
    // explicit that this is "NOT a backend authorization change." That means a role holding e.g. only
    // epic.view will ALWAYS also carry workflow.view in its stored grant — so "does this caller hold any
    // workflow.* code" can never be used alone to mean "sees every workflow regardless of vendor": it
    // would be true for essentially every role that has any vendor permission at all, defeating row-level
    // filtering entirely. A caller only gets the broad "see everything" treatment here when they hold a
    // workflow.* code AND no vendor-group permission at all — i.e. workflow.view was actually granted in
    // its own right (via the Workflow row directly), not merely implied by a vendor checkbox.
    private static bool HasGlobalWorkflowVisibility(IReadOnlyList<string> permissions)
        => HasBlanketWorkflowAccess(permissions) && !HasAnyVendorGroupPermission(permissions);

    private static bool HasBlanketWorkflowAccess(IReadOnlyList<string> permissions)
        => permissions.Any(code => code.StartsWith("workflow.", StringComparison.OrdinalIgnoreCase));

    // SourceSystemPermissionGroups.GroupFor falls back to the generic PermissionGroupCode.SourceConnections
    // for a vendor/destination-type enum value with no same-named group of its own (e.g. DestinationType.
    // Medplum — its dedicated group was removed the same way NewEHR/Hl7v2 were, leaving the type itself
    // still selectable on a node but permission-wise ungated). That fallback is the generic Settings-page
    // "Source Connections" permission, not a stand-in for the vendor's own permission — treating it as
    // this workflow's "used group" would mean anyone holding the unrelated sourceconnections.view
    // permission (a very common grant) could see every workflow that happens to use an ungated vendor
    // type, defeating the filter. WorkflowModuleAccessAuthorizationHandler.NodeGroupPrefixes excludes
    // this same fallback for the identical reason (via SourceSystemPermissionGroups.AllGroupsFor) — kept
    // in sync here rather than shared, matching this file's existing tolerance for small duplication.
    private static void AddVendorGroupIfSpecific(HashSet<PermissionGroupCode> usedGroups, Enum vendorType)
    {
        var group = SourceSystemPermissionGroups.GroupFor(vendorType);
        if (group != PermissionGroupCode.SourceConnections)
        {
            usedGroups.Add(group);
        }
    }

    private static bool HasAnyVendorGroupPermission(IReadOnlyList<string> permissions)
        => permissions.Any(code =>
        {
            var dot = code.IndexOf('.');
            var group = dot >= 0 ? code[..dot] : code;
            return NodeGroupPrefixes.Value.Contains(group);
        });

    // True if the caller holds any action (view/create/edit/delete/execute) for the given vendor group —
    // deliberately not just `.view`, so a role scoped to e.g. epic.create (but not epic.view) still sees
    // the Epic-sourced workflows it's otherwise allowed to reach via the module-access gate.
    private static bool HasAnyActionFor(IReadOnlyList<string> permissions, PermissionGroupCode group)
    {
        var prefix = group.ToString().ToLowerInvariant() + ".";
        return permissions.Any(code => code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Records a refused anonymous <c>latest-launch-result</c> read into the SMART launch audit trail, with the
    /// workflow id as the correlation id.
    ///
    /// The correlation id is what Governance > Correlation Search matches on
    /// (<c>EfGovernanceQueryService.GetCorrelationSearchResultAsync</c> filters SmartLaunchLogs by it), and a
    /// refusal happens before any run exists, so there is no run correlation id to attach. Stamping the workflow
    /// id means an admin handed nothing but "my integration gets a 404" can paste that id and see exactly which
    /// gate rejected the call — the reason never goes back to the anonymous caller on purpose, since telling it
    /// apart from "no such workflow" would leak whether a given workflow exists.
    /// </summary>
    private static async Task LogRefusedLaunchResultAsync(
        IGovernanceLogger governanceLogger, Guid workflowRunId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await governanceLogger.LogSmartLaunchAsync(
                new SmartLaunchEntry(
                    Guid.Empty,
                    $"workflowRun:{workflowRunId}",
                    "LaunchResult",
                    Success: false,
                    FailureReason: reason,
                    CorrelationId: workflowRunId.ToString()),
                cancellationToken);
        }
        catch (Exception)
        {
            // Audit-trail best effort: a logging failure must never turn a clean 404 into a 500.
        }
    }

    private static async Task LogRefusedLatestLaunchResultAsync(
        IGovernanceLogger governanceLogger, Guid workflowId, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await governanceLogger.LogSmartLaunchAsync(
                new SmartLaunchEntry(
                    Guid.Empty,
                    $"workflow:{workflowId}",
                    "LatestLaunchResult",
                    Success: false,
                    FailureReason: reason,
                    CorrelationId: workflowId.ToString()),
                cancellationToken);
        }
        catch (Exception)
        {
            // Audit-trail best effort: a logging failure must never turn a clean 404 into a 500.
        }
    }

    private static bool TryGetConfigurationGuid(string? configurationJson, string key, out Guid value)
    {
        value = Guid.Empty;
        return TryParseConfigurationSettings(configurationJson) is { } config
            && config[key]?.ToString() is { } raw
            && Guid.TryParse(raw, out value);
    }

    private static string? GetConfigurationString(string? configurationJson, string key)
        => TryParseConfigurationSettings(configurationJson) is { } config ? config[key]?.ToString() : null;

    // Used by the node-removal permission check above: every node whose config carries `key` (sourceConnectionId
    // or destinationId) as a valid Guid, across a set of nodes' raw ConfigurationJson strings. A mapping/merge
    // node with neither key simply contributes nothing — no need to filter by node category first.
    private static HashSet<Guid> CollectConfigurationGuids(IEnumerable<string?> configurationJsons, string key)
    {
        var ids = new HashSet<Guid>();
        foreach (var json in configurationJsons)
        {
            if (TryGetConfigurationGuid(json, key, out var value))
            {
                ids.Add(value);
            }
        }
        return ids;
    }

    // Finds the mappingProfileIds map already persisted on whichever existing node targets this same
    // destination, keyed by resource type — see the seeding comment at its call site in BuildWorkflow.
    // destinationId (not node.Id) is the only thing that reliably identifies "the same logical node" across
    // saves, since AddNode mints a fresh row id every time.
    private static Dictionary<string, string> SeedExistingMappingProfileIds(WorkflowDefinition? existingDefinition, Guid destinationId)
    {
        var seeded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existingDefinition is null)
        {
            return seeded;
        }

        foreach (var node in existingDefinition.Nodes)
        {
            if (!TryGetConfigurationGuid(node.ConfigurationJson, "destinationId", out var nodeDestinationId)
                || nodeDestinationId != destinationId)
            {
                continue;
            }

            if (TryParseConfigurationSettings(node.ConfigurationJson)?["mappingProfileIds"] is JsonObject idsByResource)
            {
                foreach (var entry in idsByResource)
                {
                    if (entry.Value?.ToString() is { } id)
                    {
                        seeded[entry.Key] = id;
                    }
                }
            }
        }

        return seeded;
    }

    // A node can carry the legacy single mappingProfileId, the per-resource mappingProfileIds map, or both (see
    // the "Kept for backward compatibility" comment in BuildWorkflow) — collect ids from whichever are present.
    private static IEnumerable<Guid> GetMappingProfileIdsFromConfiguration(string? configurationJson)
    {
        var config = TryParseConfigurationSettings(configurationJson);
        if (config is null)
        {
            yield break;
        }

        if (config["mappingProfileId"]?.ToString() is { } singleId && Guid.TryParse(singleId, out var parsedSingleId))
        {
            yield return parsedSingleId;
        }

        if (config["mappingProfileIds"] is JsonObject idsByResource)
        {
            foreach (var entry in idsByResource)
            {
                if (entry.Value?.ToString() is { } rawId && Guid.TryParse(rawId, out var parsedId))
                {
                    yield return parsedId;
                }
            }
        }
    }

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

    /// <summary>
    /// A node's settings for READING: the <c>config</c> object when the node is enveloped (plan §3), otherwise
    /// the root itself.
    ///
    /// Deliberately separate from <see cref="TryParseConfiguration"/>, which returns the ROOT and is what
    /// mutation paths must use. <see cref="WithConfiguration"/> re-serializes whatever it is handed as the whole
    /// node configuration, so resolving the envelope there would save the inner object as the entire config and
    /// silently drop the envelope and every sibling key with it.
    /// </summary>
    private static JsonObject? TryParseConfigurationSettings(string? configurationJson)
    {
        if (TryParseConfiguration(configurationJson) is not { } root)
        {
            return null;
        }

        // Only an object counts as an envelope: a wizard field bag is Record<string,string>, so a legacy node
        // can carry a STRING called "config" and must still be read flat.
        return root[WorkflowNodeConfigurationEnvelope.ConfigProperty] as JsonObject ?? root;
    }

    /// <summary>Node types whose executors resolve workflow-scoped transformation rules, and so must know which
    /// workflow they belong to.</summary>
    private static readonly HashSet<string> RuleResolvingNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        WorkflowNodeTypes.Mapping,
        WorkflowNodeTypes.FhirResourceTransform,
        WorkflowNodeTypes.DeIdentification,
    };

    /// <summary>
    /// Stamps the workflow's own id onto every rule-resolving node, under the key those executors read
    /// (<c>resourcePipelineRouteId</c>).
    ///
    /// Done HERE rather than in the client because the client does not reliably know the id: on a first save
    /// the workflow has none yet, so WorkflowGraphMapperServiceV2's own stamping loop skips the key entirely and
    /// the node is persisted without it. The executor then cannot tell which workflow it is running for, every
    /// workflow-scoped rule misses, and the resource is written untransformed — silently, with the run still
    /// reporting success. That is how a DateMathAge rule went missing and a raw birthDate reached an int column.
    ///
    /// The server always knows the id, so stamping at the persistence choke point fixes first save and re-save
    /// alike. An id the client already set is left alone.
    /// </summary>
    /// <summary>
    /// Replaces any mapping field's <c>JsonPath</c> that is missing its array wildcards with the FHIR element
    /// catalog's own pre-computed path, matched on the field's source FHIR path.
    ///
    /// Only fields whose stored path contains no <c>[*]</c> are considered, and a field is only rewritten when
    /// the catalog knows that exact element AND its own path actually differs — so a correct path (the common
    /// case, where the wizard's catalog had loaded) is never touched, and an unknown/custom element is left
    /// exactly as the client sent it rather than guessed at.
    /// </summary>
    private static async Task<IReadOnlyCollection<MappingBuildSpec>?> RepairMappingJsonPathsAsync(
        WorkflowBuildRequest request,
        IConfigurationRepository configurationRepository,
        IServiceProvider serviceProvider,
        IFhirElementCatalog genericFhirCatalog,
        CancellationToken cancellationToken)
    {
        if (request.Mappings is not { Count: > 0 } specs)
        {
            return request.Mappings;
        }

        var catalog = await ResolveBuildCatalogAsync(
            request, configurationRepository, serviceProvider, genericFhirCatalog, cancellationToken);

        var repaired = new List<MappingBuildSpec>(specs.Count);
        foreach (var spec in specs)
        {
            // Indexed per resource type, not per field: Fields(resourceType) walks the whole catalog.
            var byFhirPath = catalog.Fields(spec.ResourceType)
                .GroupBy(element => element.FhirPath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var fields = spec.Fields
                .Select(field => RepairFieldJsonPath(field, spec.ResourceType, byFhirPath))
                .ToArray();

            repaired.Add(spec with { Fields = fields });
        }

        return repaired;
    }

    private static MappingFieldDto RepairFieldJsonPath(
        MappingFieldDto field,
        string resourceType,
        IReadOnlyDictionary<string, FhirElementDto> catalogByFhirPath)
    {
        if (string.IsNullOrWhiteSpace(field.JsonPath) || field.JsonPath.Contains("[*]", StringComparison.Ordinal))
        {
            return field;
        }

        // "$.name.family" -> "name.family", the shape the catalog keys its elements by (it stores FhirPath
        // without the resource-type prefix). A joined/aggregate path ("a|b") or the whole-document "$" has no
        // single element to match and falls out here.
        var candidate = field.JsonPath.StartsWith("$.", StringComparison.Ordinal)
            ? field.JsonPath[2..]
            : null;
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Contains('|', StringComparison.Ordinal))
        {
            return field;
        }

        if (!catalogByFhirPath.TryGetValue(candidate, out var element)
            && !catalogByFhirPath.TryGetValue($"{resourceType}.{candidate}", out element))
        {
            return field;
        }

        return string.IsNullOrWhiteSpace(element.JsonPath)
            || string.Equals(element.JsonPath, field.JsonPath, StringComparison.Ordinal)
                ? field
                : field with { JsonPath = element.JsonPath };
    }

    /// <summary>The catalog for whichever vendor this build's source connections speak — mirrors
    /// MappingController.ResolveCatalogAsync. Falls back to the generic R4 catalog when no source is
    /// resolvable yet (a brand-new workflow whose source connection this same request is about to create).</summary>
    private static async Task<IFhirElementCatalog> ResolveBuildCatalogAsync(
        WorkflowBuildRequest request,
        IConfigurationRepository configurationRepository,
        IServiceProvider serviceProvider,
        IFhirElementCatalog genericFhirCatalog,
        CancellationToken cancellationToken)
    {
        foreach (var source in request.Sources ?? [])
        {
            if (source.ExistingId is { } existingId)
            {
                var connection = await configurationRepository.GetSourceConnectionAsync(existingId, cancellationToken);
                if (connection is not null)
                {
                    return serviceProvider.GetRequiredKeyedService<IFhirElementCatalog>(
                        FhirElementCatalogKeys.For(connection.SourceSystemType));
                }
            }

            // Not yet created (this request creates it) — the spec already names the vendor it will be.
            return serviceProvider.GetRequiredKeyedService<IFhirElementCatalog>(
                FhirElementCatalogKeys.For(source.Source.SourceSystemType));
        }

        return genericFhirCatalog;
    }

    private static string StampWorkflowId(string? configurationJson, string nodeType, Guid workflowId)
    {
        if (!RuleResolvingNodeTypes.Contains(nodeType))
        {
            return configurationJson ?? "{}";
        }

        // Written to the ROOT, not through the envelope resolver: this is a mutation, and the executors read
        // the key from whichever shape the node is in (see WorkflowNodeConfigurationEnvelope).
        //
        // Unparseable config is returned exactly as it arrived rather than replaced with a bare stamped
        // object — the same "never make a bad save worse" stance the rest of this file's configuration
        // helpers take. Stamping over it would silently discard whatever the node actually held.
        if (TryParseConfiguration(configurationJson) is not { } config)
        {
            return configurationJson ?? "{}";
        }

        if (config["resourcePipelineRouteId"]?.ToString() is { Length: > 0 })
        {
            return configurationJson ?? "{}";
        }

        config["resourcePipelineRouteId"] = workflowId.ToString();
        return config.ToJsonString();
    }

    private static WorkflowDefinition BuildWorkflow(Guid workflowId, WorkflowDefinitionRequest request, int version = 1)
    {
        var workflow = new WorkflowDefinition(
            workflowId, request.Name, version: 1, request.IsEnabled, request.IsPubliclyLaunchable, request.Description);
        var nodeIdsByClientId = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var nodeRequest in request.Nodes)
        {
            var node = workflow.AddNode(
                nodeRequest.NodeType,
                nodeRequest.Category,
                nodeRequest.Rank,
                nodeRequest.SubRank,
                nodeRequest.DisplayName,
                StampWorkflowId(nodeRequest.ConfigurationJson, nodeRequest.NodeType, workflowId),
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
            request.BackfillOnFirstRun,
            request.TimeZoneId);
    }
}
