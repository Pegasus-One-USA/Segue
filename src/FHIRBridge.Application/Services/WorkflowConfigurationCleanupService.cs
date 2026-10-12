using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Runtime.Application.Workflows.Catalog;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Application.Services;

/// <inheritdoc />
public sealed class WorkflowConfigurationCleanupService : IWorkflowConfigurationCleanupService
{
    private readonly IConfigurationRepository _configurationRepository;
    private readonly ITransformationRuleRepository _transformationRuleRepository;
    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;
    private readonly IDeIdentificationProfileService? _deIdentificationProfileService;
    private readonly ILogger<WorkflowConfigurationCleanupService> _logger;

    public WorkflowConfigurationCleanupService(
        IConfigurationRepository configurationRepository,
        ITransformationRuleRepository transformationRuleRepository,
        IWorkflowDefinitionStore workflowDefinitionStore,
        IDeIdentificationProfileService? deIdentificationProfileService = null,
        ILogger<WorkflowConfigurationCleanupService>? logger = null)
    {
        _configurationRepository = configurationRepository;
        _transformationRuleRepository = transformationRuleRepository;
        _workflowDefinitionStore = workflowDefinitionStore;
        _deIdentificationProfileService = deIdentificationProfileService;
        _logger = logger ?? NullLogger<WorkflowConfigurationCleanupService>.Instance;
    }

    public async Task<WorkflowConfigurationCleanupResult> SoftDeleteUnreferencedAsync(
        Guid workflowId,
        WorkflowDefinition? previousDefinition,
        IReadOnlyCollection<SavedWorkflowNode> newNodes,
        CancellationToken cancellationToken)
    {
        if (previousDefinition is null)
        {
            return WorkflowConfigurationCleanupResult.Empty;
        }

        var newNodeConfigurationJson = newNodes.Select(node => node.ConfigurationJson).ToArray();

        // The rules a removed Transformation / De-identification step stood for. Decided here — from the stored
        // definition this save replaces and the one it stores — rather than when the step is deleted on the
        // canvas, so they go only if, and when, the workflow without that step is actually saved: an unsaved
        // canvas, a failed save (this runs inside the save's transaction) or a step put back before saving all
        // leave them in place, and the saved workflow never points at rules that are gone.
        var transformationStepRulesDeleted = await DeleteRulesOfRemovedTransformationStepsAsync(
            workflowId, previousDefinition, newNodes, cancellationToken);
        var (deIdentificationRulesDeleted, keptBecauseUsedBy) = await DeleteRulesOfRemovedDeIdentificationAsync(
            workflowId, previousDefinition, newNodes, cancellationToken);
        var stepResult = new WorkflowConfigurationCleanupResult(
            [], [], transformationStepRulesDeleted, deIdentificationRulesDeleted, keptBecauseUsedBy);

        var previousConfigurationJson = previousDefinition.Nodes.Select(node => node.ConfigurationJson).ToArray();

        var removedDestinationIds = CollectGuids(previousConfigurationJson, "destinationId");
        removedDestinationIds.ExceptWith(CollectGuids(newNodeConfigurationJson, "destinationId"));

        var removedMappingProfileIds = CollectMappingProfileIds(previousConfigurationJson);
        removedMappingProfileIds.ExceptWith(CollectMappingProfileIds(newNodeConfigurationJson));

        if (removedDestinationIds.Count == 0 && removedMappingProfileIds.Count == 0)
        {
            return stepResult;
        }

        // Every id any OTHER stored workflow still points at. A destination connection is a Settings-level
        // master record that several workflows can share (the "Existing connection" picker), so this save
        // dropping it says nothing about whether it is still in use — and a mapping profile promoted to
        // master (see ConfigurationService.PromoteMappingProfileToMasterAsync) can be shared the same way.
        var referencedMappingProfileIds =
            await CollectMappingProfileReferencesFromOtherWorkflowsAsync(workflowId, cancellationToken);

        var retiredMappingProfileIds = await RetireMappingProfilesAsync(
            removedMappingProfileIds, removedDestinationIds, referencedMappingProfileIds, cancellationToken);

        var retiredRuleIds = await RetireWorkflowScopedRulesAsync(
            workflowId, removedDestinationIds, newNodeConfigurationJson, cancellationToken);

        return stepResult with { MappingProfileIds = retiredMappingProfileIds, TransformationRuleIds = retiredRuleIds };
    }

    /// <summary>
    /// A Transformation step removed while its destination stays: that destination's Workflow-scoped rules for
    /// this workflow are deleted (the destination itself going is handled by RetireWorkflowScopedRulesAsync).
    /// Rules that record no destination are taken only when no other destination of the same type is left,
    /// since they could belong to it.
    /// </summary>
    private async Task<int> DeleteRulesOfRemovedTransformationStepsAsync(
        Guid workflowId,
        WorkflowDefinition previousDefinition,
        IReadOnlyCollection<SavedWorkflowNode> newNodes,
        CancellationToken cancellationToken)
    {
        var previousStepDestinations = CollectGuids(previousDefinition.Nodes
            .Where(node => node.NodeType == WorkflowNodeTypes.FhirResourceTransform)
            .Select(node => node.ConfigurationJson), "destinationId");
        if (previousStepDestinations.Count == 0)
        {
            return 0;
        }

        var newStepDestinations = CollectGuids(newNodes
            .Where(node => node.NodeType == WorkflowNodeTypes.FhirResourceTransform)
            .Select(node => node.ConfigurationJson), "destinationId");
        var newDestinations = CollectGuids(newNodes.Select(node => node.ConfigurationJson), "destinationId");

        var deleted = 0;
        foreach (var destinationId in previousStepDestinations)
        {
            if (newStepDestinations.Contains(destinationId) || !newDestinations.Contains(destinationId)
                || await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken) is not { } destination)
            {
                continue;
            }

            var otherOfSameType = false;
            foreach (var otherId in newDestinations.Where(id => id != destinationId))
            {
                if ((await _configurationRepository.GetDestinationAsync(otherId, cancellationToken))?.DestinationType
                    == destination.DestinationType)
                {
                    otherOfSameType = true;
                    break;
                }
            }

            deleted += await _transformationRuleRepository.DeleteWorkflowRulesForDestinationAsync(
                workflowId, destinationId, destination.DestinationType, includeUnattributed: !otherOfSameType,
                cancellationToken);
        }

        return deleted;
    }

    /// <summary>
    /// The workflow's last De-identification step removed: the rules of the policy it owns are deleted — unless
    /// anything outside this workflow still uses that policy, in which case they are kept and the reason logged
    /// (the save itself goes ahead). See IDeIdentificationProfileService.DeleteWorkflowPolicyRulesAsync.
    /// </summary>
    private async Task<(int Deleted, IReadOnlyList<string>? KeptBecauseUsedBy)> DeleteRulesOfRemovedDeIdentificationAsync(
        Guid workflowId,
        WorkflowDefinition previousDefinition,
        IReadOnlyCollection<SavedWorkflowNode> newNodes,
        CancellationToken cancellationToken)
    {
        if (_deIdentificationProfileService is null
            || !previousDefinition.Nodes.Any(node => node.NodeType == WorkflowNodeTypes.DeIdentification)
            || newNodes.Any(node => node.NodeType == WorkflowNodeTypes.DeIdentification))
        {
            return (0, null);
        }

        // The previous definition's destinations too: the save has already replaced this workflow's stored nodes, so
        // a destination removed together with its De-identification step is referenced only there.
        var previousDestinationIds = CollectGuids(
            previousDefinition.Nodes.Select(node => node.ConfigurationJson), "destinationId");
        var result = await _deIdentificationProfileService.DeleteWorkflowPolicyRulesAsync(
            workflowId, cancellationToken, previousDestinationIds);
        if (result.UsedElsewhere.Count == 0)
        {
            return (result.Deleted, null);
        }

        _logger.LogWarning(
            "Workflow {WorkflowId} no longer has a De-identification step, but its de-identification policy is still "
            + "used by {UsedBy}, so its rules were kept.",
            workflowId, string.Join(", ", result.UsedElsewhere));
        return (0, result.UsedElsewhere);
    }

    /// <summary>
    /// Soft-deletes the mapping profiles this workflow has let go of: the ones its own nodes named, plus
    /// any other profile still targeting a destination it just dropped (a profile created by the
    /// mapping-config import whose id never made it back onto a node). Anything another workflow points at
    /// is left alone.
    /// </summary>
    private async Task<List<Guid>> RetireMappingProfilesAsync(
        HashSet<Guid> removedMappingProfileIds,
        HashSet<Guid> removedDestinationIds,
        HashSet<Guid> referencedMappingProfileIds,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<Guid, MappingProfile>();

        foreach (var mappingProfileId in removedMappingProfileIds)
        {
            if (await _configurationRepository.GetMappingProfileAsync(mappingProfileId, cancellationToken) is { } profile)
            {
                candidates[profile.Id] = profile;
            }
        }

        foreach (var destinationId in removedDestinationIds)
        {
            foreach (var profile in await _configurationRepository.GetMappingProfilesByDestinationAsync(
                         destinationId, cancellationToken))
            {
                candidates[profile.Id] = profile;
            }
        }

        var retired = new List<Guid>();
        foreach (var (id, profile) in candidates)
        {
            if (referencedMappingProfileIds.Contains(id))
            {
                continue;
            }

            // Soft delete regardless of pipeline run history: the row and its id stay resolvable, so the
            // runs that used it keep resolving — it just stops appearing in the mapping-profile lists.
            await _configurationRepository.RemoveMappingProfileAsync(profile, cancellationToken);
            retired.Add(id);
        }

        return retired;
    }

    /// <summary>
    /// Soft-deletes this workflow's own Workflow-scoped transformation rules for destination TYPES it no
    /// longer writes to.
    /// </summary>
    /// <remarks>
    /// Destination type, not destination id, because that is the only destination identity a rule carries
    /// (see TransformationRule / EfTransformationRuleRepository.GetWorkflowScopedAsync, whose Workflow-tier
    /// predicate is workflow + resource type + destination field). So swapping a SQL Server destination for
    /// a Mongo one retires the SQL rules, while replacing it with another SQL Server destination keeps them
    /// — for that type they are still this workflow's live rules, and the resolver would still apply them.
    ///
    /// Only the Workflow tier is touched. Field / ResourceType / DestinationType / Global rules are
    /// tenant-wide and shared with every other pipeline, so a canvas edit must never retire them.
    /// </remarks>
    private async Task<List<Guid>> RetireWorkflowScopedRulesAsync(
        Guid workflowId,
        HashSet<Guid> removedDestinationIds,
        IReadOnlyCollection<string?> newNodeConfigurationJson,
        CancellationToken cancellationToken)
    {
        var removedTypes = new HashSet<DestinationType>();
        foreach (var destinationId in removedDestinationIds)
        {
            if (await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken) is { } destination)
            {
                removedTypes.Add(destination.DestinationType);
            }
        }

        if (removedTypes.Count == 0)
        {
            return [];
        }

        foreach (var destinationId in CollectGuids(newNodeConfigurationJson, "destinationId"))
        {
            if (await _configurationRepository.GetDestinationAsync(destinationId, cancellationToken) is { } surviving)
            {
                removedTypes.Remove(surviving.DestinationType);
            }
        }

        var retired = new List<Guid>();
        foreach (var destinationType in removedTypes)
        {
            foreach (var rule in await ListWorkflowScopedRulesAsync(workflowId, destinationType, cancellationToken))
            {
                await _transformationRuleRepository.DeleteAsync(rule, cancellationToken);
                retired.Add(rule.Id);
            }
        }

        return retired;
    }

    /// <summary>
    /// Every phase of one workflow's Workflow-scoped rules for a destination type. Two calls because
    /// ListAsync's null <c>executionPhase</c> deliberately means "everything except FhirResource" (see its
    /// own remarks) rather than "no phase filter".
    /// </summary>
    private async Task<List<TransformationRule>> ListWorkflowScopedRulesAsync(
        Guid workflowId, DestinationType destinationType, CancellationToken cancellationToken)
    {
        var rules = new List<TransformationRule>();
        foreach (var phase in new TransformExecutionPhase?[] { null, TransformExecutionPhase.FhirResource })
        {
            rules.AddRange(await _transformationRuleRepository.ListAsync(
                TransformScope.Workflow, destinationType, resourceType: null, destinationField: null,
                resourcePipelineRouteId: workflowId, sourceSystem: null, sourceField: null,
                executionPhase: phase, cancellationToken));
        }

        return rules;
    }

    private async Task<HashSet<Guid>> CollectMappingProfileReferencesFromOtherWorkflowsAsync(
        Guid workflowId, CancellationToken cancellationToken)
    {
        var configurationJson = (await _workflowDefinitionStore.ListAsync(cancellationToken))
            .Where(definition => definition.Id != workflowId)
            .SelectMany(definition => definition.Nodes)
            .Select(node => node.ConfigurationJson)
            .ToArray();

        return CollectMappingProfileIds(configurationJson);
    }

    /// <summary>
    /// A node names its profiles two ways: <c>mappingProfileIds</c>, an object keyed by resource type, and
    /// the legacy singular <c>mappingProfileId</c> for the primary resource. Both are read — a node saved
    /// before the map existed only carries the singular one.
    /// </summary>
    private static HashSet<Guid> CollectMappingProfileIds(IEnumerable<string?> configurationJsons)
    {
        var ids = new HashSet<Guid>();
        foreach (var configurationJson in configurationJsons)
        {
            if (Parse(configurationJson) is not { } configuration)
            {
                continue;
            }

            if (configuration["mappingProfileId"]?.ToString() is { } singular && Guid.TryParse(singular, out var id))
            {
                ids.Add(id);
            }

            if (configuration["mappingProfileIds"] is JsonObject byResource)
            {
                foreach (var entry in byResource)
                {
                    if (entry.Value?.ToString() is { } raw && Guid.TryParse(raw, out var mapped))
                    {
                        ids.Add(mapped);
                    }
                }
            }
        }

        return ids;
    }

    private static HashSet<Guid> CollectGuids(IEnumerable<string?> configurationJsons, string key)
    {
        var ids = new HashSet<Guid>();
        foreach (var configurationJson in configurationJsons)
        {
            if (Parse(configurationJson)?[key]?.ToString() is { } raw && Guid.TryParse(raw, out var id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private static JsonObject? Parse(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(configurationJson) is not JsonObject root)
            {
                return null;
            }

            // Dual-read (plan §3): resolve an enveloped node's "config" object, falling back to the root for the
            // legacy flat shape. This service is slated for deletion once nodes carry their own configuration
            // (plan §5) — until then it must still see the ids it is deciding about, or a migrated workflow's
            // mapping profiles would look unreferenced and be soft-deleted out from under it.
            return root[WorkflowNodeConfigurationEnvelope.ConfigProperty] is JsonObject settings ? settings : root;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
