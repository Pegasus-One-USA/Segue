using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Services.Workflows;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FHIRBridge.SharedKernel.Enums;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// See <see cref="IEpicSourceConnectionScopeSyncService"/>. The resource types a connection is scoped for are the
/// ones its SOURCE nodes declare they read ("Resources", see <see cref="WorkflowNodeResourceTypes.ReadSourceDeclared"/>),
/// unioned across every source node in every workflow that references the connection, so two workflows sharing one
/// connection never overwrite each other's scopes (the last-save-wins drift this service was built to replace).
/// Destinations choose from their source's list and can never widen it, so they no longer drive scopes; only a legacy
/// source node with no declared list falls back to what the destinations reachable from it write ("dest_resources" plus
/// the "dest_mappings" rows, as the portal's build does) — or, for a legacy node with no outgoing edges, what every
/// destination in its workflow writes. Auto-fetch reference targets are the one deliberate widening (see
/// GetUsedResourceTypes).
/// A node is legacy unless it carries the "Resource types declared" marker (or is a CSV / SQL Table node): an
/// unmarked "Resources" list (the Generic FHIR form's silent 12-type default, say) never drives scopes.
/// </summary>
public sealed class EpicSourceConnectionScopeSyncService : IEpicSourceConnectionScopeSyncService
{
    // Any source node, not just "EpicSourceNode". Every vendor used to persist under that single NodeType, so
    // matching it literally was enough; a node saved since now carries its own vendor type (AthenahealthSourceNode,
    // EClinicalWorksSourceNode, ...) and a literal match would silently stop finding the workflows that reference
    // this connection — leaving an athenahealth connection's scopes frozen at whatever they were, which is the one
    // vendor whose token request fails outright on a stale resource list (see the athenahealth note in SyncAsync).
    // Matched by suffix rather than an enumerated list so a vendor added to the catalog later needs no change here.
    private const string SourceNodeTypeSuffix = "SourceNode";
    private const string SourceConnectionIdConfigKey = "sourceConnectionId";
    private const string AutoFetchMissingReferencesConfigKey = "dest_autoFetchMissingReferences";

    private readonly IWorkflowDefinitionStore _workflowStore;
    private readonly IConfigurationRepository _configurationRepository;
    private readonly IScopeGeneratorService _scopeGenerator;
    private readonly ILogger<EpicSourceConnectionScopeSyncService> _logger;

    public EpicSourceConnectionScopeSyncService(
        IWorkflowDefinitionStore workflowStore,
        IConfigurationRepository configurationRepository,
        IScopeGeneratorService scopeGenerator,
        ILogger<EpicSourceConnectionScopeSyncService> logger)
    {
        _workflowStore = workflowStore;
        _configurationRepository = configurationRepository;
        _scopeGenerator = scopeGenerator;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>?> SyncAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
    {
        var sourceConnection = await _configurationRepository.GetSourceConnectionAsync(sourceConnectionId, cancellationToken);
        if (sourceConnection is null)
        {
            return null;
        }

        // A write-only connection is never a source node's connection, so the resource types "used" by it are always
        // none; regenerating its scopes from that would strip the scopes its write token is requested with.
        if (!sourceConnection.Access.AllowsRead())
        {
            return null;
        }

        // athenahealth is exempt from the Interactive-only gate below: unlike every other vendor here, its Backend
        // System connections ALSO need their resource-type selection kept in sync with what's actually consumed
        // downstream — SourceConnectionRuntimeResolver regenerates scopes fresh from Retrieval.ResourceTypes on
        // every run (never trusting a stored Authentication.Scopes snapshot for a connection with retrieval
        // config), specifically because a broad/stale resource list gets the WHOLE token request rejected by
        // athenahealth's authorization server. This is what makes "create a connection, then reuse it as Existing
        // Source in a workflow" actually work for athenahealth — this sync already runs unconditionally on every
        // sourceConnectionId a build references (WorkflowEndpoints' /workflows/build handler), whether that node
        // built a fresh connection or pointed at an existing one, so it's the correct place to keep an existing
        // connection's Retrieval current too, not just its Scopes.
        var isAthenahealthBackend = sourceConnection.SourceSystemType == SourceSystemType.Athenahealth;
        if (sourceConnection.Interactive is null && !isAthenahealthBackend)
        {
            // Every other vendor's Backend Services sources have no Interactive configuration and aren't driven by
            // the workflow's resource-type picker the same way — leave their scopes exactly as configured.
            return null;
        }

        var workflows = await _workflowStore.ListAsync(cancellationToken);
        var usedResourceTypes = GetUsedResourceTypes(
            workflows,
            sourceConnectionId,
            sourceConnection.ApplicationType,
            VendorResourceTypeSupport.For(sourceConnection.SourceSystemType),
            out var unsupportedDeclared);
        if (unsupportedDeclared.Count > 0)
        {
            // A declared type the vendor cannot serve would put an unregistered scope in the token request, and
            // athenahealth / eClinicalWorks reject the WHOLE request for one (invalid_scope). The portal picker only
            // offers supported types, so this guards hand-authored or API-built nodes.
            _logger.LogWarning(
                "Source connection {SourceConnectionId} ({Vendor}): left out declared resource types the vendor does " +
                "not support: [{UnsupportedTypes}].",
                sourceConnectionId, sourceConnection.SourceSystemType, string.Join(", ", unsupportedDeclared));
        }

        // eClinicalWorks (Healow) has the same hard v1-only requirement as athenahealth (confirmed against a live
        // authorize attempt, which eCW rejected with invalid_scope for a v2/.rs resource scope) — without this,
        // DetectScopeVersionFromExistingScopes below just re-derives "v2" from whatever .rs scope is already
        // stored and re-persists it unchanged on every workflow save that touches this connection, permanently
        // perpetuating a wrong scope no amount of re-saving the connection's own wizard form can ever break out of.
        var scopeVersion = isAthenahealthBackend || sourceConnection.SourceSystemType == SourceSystemType.Healow
            ? "v1"
            : DetectScopeVersionFromExistingScopes(sourceConnection.Authentication.Scopes);
        var generated = _scopeGenerator.Generate(
            sourceConnection.ApplicationType,
            usedResourceTypes,
            scopeVersion,
            scopeVersionDetected: false,
            supportedScopes: null,
            vendor: sourceConnection.SourceSystemType);

        var retrievalChanged = false;
        if (isAthenahealthBackend && usedResourceTypes.Count > 0)
        {
            var existingRetrieval = sourceConnection.Retrieval;
            if (existingRetrieval is null || !existingRetrieval.ResourceTypes.SequenceEqual(usedResourceTypes, StringComparer.OrdinalIgnoreCase))
            {
                sourceConnection.Update(
                    sourceConnection.Name,
                    sourceConnection.SourceSystemType,
                    sourceConnection.BaseUrl,
                    sourceConnection.Authentication,
                    sourceConnection.ApplicationType,
                    sourceConnection.Interactive,
                    new SourceRetrievalConfiguration(
                        existingRetrieval?.RetrievalMethod ?? "search-rest",
                        [.. usedResourceTypes],
                        existingRetrieval?.SearchCriteria,
                        existingRetrieval?.IncrementalSyncEnabled ?? false,
                        existingRetrieval?.PageSize,
                        existingRetrieval?.SortOrder,
                        existingRetrieval?.IncludeParameters,
                        existingRetrieval?.RevIncludeParameters,
                        existingRetrieval?.RetryPolicy,
                        existingRetrieval?.TimeoutSeconds,
                        existingRetrieval?.MaxRecordsPerRun,
                        existingRetrieval?.LastSuccessfulSyncUtcByResourceType,
                        existingRetrieval?.ExportScope,
                        existingRetrieval?.GroupId,
                        existingRetrieval?.PatientIds,
                        existingRetrieval?.OutputFormat));
                retrievalChanged = true;
            }
        }

        var scopesChanged = !sourceConnection.Authentication.Scopes.SequenceEqual(generated.Scopes, StringComparer.Ordinal);
        if (!scopesChanged && !retrievalChanged)
        {
            return generated.Scopes;
        }

        var previousScopes = string.Join(' ', sourceConnection.Authentication.Scopes);
        if (scopesChanged)
        {
            sourceConnection.UpdateScopes([.. generated.Scopes]);
        }

        await _configurationRepository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);

        _logger.LogInformation(
            "Synced source connection {SourceConnectionId} scopes/retrieval to match actual pipeline usage. " +
            "Used resource types: [{ResourceTypes}]. Previous scopes: \"{PreviousScopes}\". New scopes: \"{NewScopes}\". " +
            "Retrieval.ResourceTypes updated: {RetrievalChanged}.",
            sourceConnectionId, string.Join(", ", usedResourceTypes), previousScopes, generated.ScopeString, retrievalChanged);

        return generated.Scopes;
    }

    public async Task<IReadOnlyList<Guid>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var connections = await _configurationRepository.GetSourceConnectionsAsync(cancellationToken);
        var changed = new List<Guid>();

        foreach (var connection in connections.Where(c =>
                     c.Access.AllowsRead() && (c.Interactive is not null || c.SourceSystemType == SourceSystemType.Athenahealth)))
        {
            var before = string.Join(' ', connection.Authentication.Scopes);
            var after = await SyncAsync(connection.Id, cancellationToken);
            if (after is not null && string.Join(' ', after) != before)
            {
                changed.Add(connection.Id);
            }
        }

        return changed;
    }

    // Every source node across every workflow that references this connection contributes the resource types it
    // declares it reads — the union across ALL such nodes and workflows, not just one. A legacy source node with no
    // declared list contributes what the destinations reachable from it write instead (selection plus mapping rows;
    // never those of a destination fed by some other source in the same workflow).
    //
    // Either way, a reachable destination with auto-fetch on adds the types its records may reference (see
    // GetAutoFetchReferenceTargets). This is the one deliberate exception to "destinations never widen the source":
    // those types are not read by the source node (its fetch stays its declared list), they are only resolved one
    // reference at a time by the destination writer with this connection's token, which 403s without the scope. The
    // portal sends exactly the declared list at build time; this sync, which runs right after every build, is what
    // adds them, so the connection's final scopes (and athenahealth's Retrieval.ResourceTypes) carry them.
    //
    // A declared list is intersected with the vendor's known-supported types (when the vendor has such a list); the
    // types left out are reported through unsupportedDeclared. The legacy path is left exactly as it was.
    private static IReadOnlyList<string> GetUsedResourceTypes(
        IReadOnlyCollection<WorkflowDefinition> workflows,
        Guid sourceConnectionId,
        ApplicationType? applicationType,
        IReadOnlyList<string>? vendorSupported,
        out IReadOnlyList<string> unsupportedDeclared)
    {
        var resourceTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var unsupported = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var supported = vendorSupported is null ? null : new HashSet<string>(vendorSupported, StringComparer.OrdinalIgnoreCase);

        foreach (var workflow in workflows)
        {
            var referencingSourceNodes = workflow.Nodes.Where(node =>
                node.NodeType.EndsWith(SourceNodeTypeSuffix, StringComparison.OrdinalIgnoreCase) &&
                TryGetSourceConnectionId(node) == sourceConnectionId);

            foreach (var sourceNode in referencingSourceNodes)
            {
                var declared = WorkflowNodeResourceTypes.ReadSourceDeclared(sourceNode.NodeType, sourceNode.ConfigurationJson);
                var reachableDestinations = GetReachableDestinations(workflow, sourceNode, legacy: declared is null);
                if (declared is null)
                {
                    // What the destinations write: their selection plus every mapping row's resource — the same
                    // union the portal's build assembler scopes a legacy source for (destinationWrittenResourceTypes),
                    // so a type that only appears in a mapping row is not dropped by the sync after the build.
                    resourceTypes.UnionWith(reachableDestinations
                        .SelectMany(destination => WorkflowNodeResourceTypes.ReadDestinationWritten(destination.ConfigurationJson)));
                }
                else
                {
                    foreach (var type in declared)
                    {
                        if (supported is null || supported.Contains(type))
                        {
                            resourceTypes.Add(type);
                        }
                        else
                        {
                            unsupported.Add(type);
                        }
                    }
                }

                foreach (var destination in reachableDestinations)
                {
                    resourceTypes.UnionWith(GetAutoFetchReferenceTargets(destination, applicationType));
                }
            }
        }

        unsupportedDeclared = [.. unsupported];
        return [.. resourceTypes];
    }

    // The destination nodes downstream of this source node through the canvas edges — the same walk the run-time
    // source executor uses to narrow its fetch, so the scopes and the fetch agree on which destinations count.
    // A legacy source node with no outgoing edges at all keeps the behaviour from before sources declared their
    // types: every destination in its workflow counts, so re-syncing an edge-less saved workflow never narrows the
    // shared connection's scopes (or, for athenahealth, its Retrieval.ResourceTypes).
    private static IReadOnlyList<WorkflowNode> GetReachableDestinations(
        WorkflowDefinition workflow, WorkflowNode sourceNode, bool legacy)
    {
        if (legacy && !workflow.Edges.Any(edge => edge.FromNodeId == sourceNode.Id))
        {
            // Every node, as the pre-declaration sync read them: only one carrying dest_resources contributes.
            return [.. workflow.Nodes.Where(node => node.Id != sourceNode.Id)];
        }

        var reachable = new HashSet<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(sourceNode.Id);
        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            foreach (var edge in workflow.Edges.Where(e => e.FromNodeId == current))
            {
                if (reachable.Add(edge.ToNodeId))
                {
                    frontier.Enqueue(edge.ToNodeId);
                }
            }
        }

        return [.. workflow.Nodes.Where(node =>
            reachable.Contains(node.Id) && node.Category == WorkflowNodeCategory.Destination)];
    }

    private static Guid? TryGetSourceConnectionId(WorkflowNode node)
    {
        var value = TryGetConfigValue(node, SourceConnectionIdConfigKey);
        return value is not null && Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    // "Automatically fetch a missing reference from the source" (dest_autoFetchMissingReferences) pulls whatever
    // resource type a written record happens to reference (e.g. a Patient's managingOrganization/generalPractitioner)
    // — discovered reactively at write time by parsing each record's FHIR JSON, never known ahead of from config
    // alone. Scoping only for the explicitly selected/mapped types therefore isn't enough: a destination scoped for
    // Patient-only with auto-fetch on can 403 the moment it tries to pull a referenced Organization/Practitioner
    // that was never selected (see the athena-aidbox Brand/CSG/PG/Provider 403s).
    //
    // A wildcard resource scope ("system/*.read") would sidestep needing to enumerate anything, but athenahealth's
    // authorization server rejects the ENTIRE token request (401 access_denied, verified live against the sandbox)
    // the moment a wildcard resource scope appears — not just the extra access, the whole run's auth. So the
    // widened set has to stay a concrete, enumerated list of resource types, not "*". ReferenceTargetTypes below is
    // sourced from FHIR R4's own StructureDefinitions (which resource types each reference-typed element on a given
    // resource can point to) rather than hand-maintained per vendor surprise — it only needs revisiting on a FHIR
    // version change, not every time a new vendor-specific reference pattern turns up.
    //
    // Returns only the added reference targets; the destination's own selection is already within its source's
    // declared types (or, for a legacy source, added by the caller).
    private static IEnumerable<string> GetAutoFetchReferenceTargets(WorkflowNode node, ApplicationType? applicationType)
    {
        var selected = WorkflowNodeResourceTypes.ReadDestinationSelected(node.ConfigurationJson);

        // A patient-facing app never gets the widened set, however the destination is configured. The widening
        // below was verified against athenahealth's BACKEND registration, whose system/ scopes cover the referenced
        // types; a Patient registration is granted patient/ scopes for the patient's own record only, so the same
        // widening asks for patient/Organization.read + patient/Practitioner.read + patient/RelatedPerson.read
        // (FhirReferenceTargets["Patient"]) and athenahealth's all-or-nothing policy evaluation then rejects the
        // WHOLE authorize request — access_denied "Policy evaluation failed", the exact failure the PractitionerRole
        // exclusion in FhirReferenceTargets already documents, one axis over. Nothing is lost by skipping it:
        // Organization/Practitioner aren't in the patient compartment, so auto-fetching them on a patient-scoped
        // token would 403 at write time even if the scope had been granted — the destination writer surfaces that
        // as its own distinct auto-fetch scope error. Checked with an is-pattern, not a switch on ApplicationType,
        // so the engine's dispatch stays in the strategy registry per the architecture rule.
        if (applicationType is ApplicationType.Patient)
        {
            return [];
        }

        if (!string.Equals(TryGetConfigValue(node, AutoFetchMissingReferencesConfigKey), "true", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var widened = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in selected)
        {
            foreach (var referenced in FhirReferenceTargets.For(type))
            {
                widened.Add(referenced);
            }
        }

        return widened;
    }

    // Envelope-aware: an enveloped node keeps its settings under "config" (see WorkflowNodeConfigurationEnvelope).
    private static string? TryGetConfigValue(WorkflowNode node, string key) =>
        WorkflowNodeResourceTypes.ReadString(node.ConfigurationJson, key);

    // The generator's own default when nothing else is known; the persisted scope string was itself produced with
    // this same default the vast majority of the time, so re-deriving from it keeps behavior stable across a sync.
    private static string DetectScopeVersionFromExistingScopes(IReadOnlyCollection<string> existingScopes)
    {
        static string Suffix(string s) => s.Contains('.') ? s[(s.LastIndexOf('.') + 1)..].ToLowerInvariant() : string.Empty;

        if (existingScopes.Any(s => Suffix(s) is "rs" or "cruds"))
        {
            return "v2";
        }

        if (existingScopes.Any(s => Suffix(s) is "read" or "write"))
        {
            return "v1";
        }

        return "v2";
    }
}
