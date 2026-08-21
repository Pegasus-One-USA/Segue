using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// See <see cref="IEpicSourceConnectionScopeSyncService"/>. Reads the resource-type selection straight out of each
/// destination node's <see cref="WorkflowNode.ConfigurationJson"/> blob (the same field the destination wizard
/// writes as "dest_resources") rather than any per-source-node snapshot — a source node's own "Resources" field is
/// only ever a single pipeline's view and is what caused the last-save-wins drift this service replaces.
/// </summary>
public sealed class EpicSourceConnectionScopeSyncService : IEpicSourceConnectionScopeSyncService
{
    private const string EpicSourceNodeType = "EpicSourceNode";
    private const string SourceConnectionIdConfigKey = "sourceConnectionId";
    private const string DestinationResourcesConfigKey = "dest_resources";
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
            // a destination resource picker the same way — leave their scopes exactly as configured.
            return null;
        }

        var workflows = await _workflowStore.ListAsync(cancellationToken);
        var usedResourceTypes = GetUsedResourceTypes(workflows, sourceConnectionId);

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
            supportedScopes: null);

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

        foreach (var connection in connections.Where(c => c.Interactive is not null || c.SourceSystemType == SourceSystemType.Athenahealth))
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

    // Every EpicSourceNode across every workflow that references this connection contributes its sibling
    // destination nodes' selected resource types — the union across ALL such workflows, not just one.
    private static IReadOnlyList<string> GetUsedResourceTypes(
        IReadOnlyCollection<WorkflowDefinition> workflows, Guid sourceConnectionId)
    {
        var resourceTypes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workflow in workflows)
        {
            var referencesThisConnection = workflow.Nodes.Any(node =>
                string.Equals(node.NodeType, EpicSourceNodeType, StringComparison.OrdinalIgnoreCase) &&
                TryGetSourceConnectionId(node) == sourceConnectionId);

            if (!referencesThisConnection)
            {
                continue;
            }

            foreach (var node in workflow.Nodes)
            {
                foreach (var resourceType in GetDestinationResourceTypes(node))
                {
                    resourceTypes.Add(resourceType);
                }
            }
        }

        return [.. resourceTypes];
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
    private static IEnumerable<string> GetDestinationResourceTypes(WorkflowNode node)
    {
        var raw = TryGetConfigValue(node, DestinationResourcesConfigKey);
        var selected = string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!string.Equals(TryGetConfigValue(node, AutoFetchMissingReferencesConfigKey), "true", StringComparison.OrdinalIgnoreCase))
        {
            return selected;
        }

        var widened = new SortedSet<string>(selected, StringComparer.OrdinalIgnoreCase);
        foreach (var type in selected)
        {
            foreach (var referenced in FhirReferenceTargets.For(type))
            {
                widened.Add(referenced);
            }
        }

        return widened;
    }

    private static string? TryGetConfigValue(WorkflowNode node, string key)
    {
        try
        {
            using var document = JsonDocument.Parse(node.ConfigurationJson);
            return document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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
