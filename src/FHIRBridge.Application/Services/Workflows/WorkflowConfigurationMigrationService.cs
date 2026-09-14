using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;

namespace FHIRBridge.Application.Services.Workflows;

/// <inheritdoc />
public sealed class WorkflowConfigurationMigrationService : IWorkflowConfigurationMigrationService
{
    /// <summary>The id properties a node can carry, and the master each one points at. Everything else in a
    /// node's config is already self-contained and simply moves under <c>config</c> unchanged.</summary>
    private const string SourceConnectionIdKey = "sourceConnectionId";
    private const string DestinationIdKey = "destinationId";
    private const string MappingProfileIdKey = "mappingProfileId";
    private const string MappingProfileIdsKey = "mappingProfileIds";

    /// <summary>The single-id keys, in the order they are reported. <see cref="MappingProfileIdsKey"/> is handled
    /// separately because it is an object of ids rather than one.</summary>
    private static readonly string[] MasterIdKeys =
        [SourceConnectionIdKey, DestinationIdKey, MappingProfileIdKey];

    private readonly IWorkflowDefinitionStore _workflowDefinitionStore;
    private readonly IConfigurationRepository _configurationRepository;

    public WorkflowConfigurationMigrationService(
        IWorkflowDefinitionStore workflowDefinitionStore,
        IConfigurationRepository configurationRepository)
    {
        _workflowDefinitionStore = workflowDefinitionStore;
        _configurationRepository = configurationRepository;
    }

    public async Task<WorkflowConfigurationMigrationResult> MigrateAsync(
        bool dryRun,
        Guid? workflowId,
        CancellationToken cancellationToken)
    {
        var workflows = workflowId is { } id
            ? [await _workflowDefinitionStore.GetAsync(id, cancellationToken)
                ?? throw new InvalidOperationException($"Workflow '{id}' does not exist.")]
            : (IReadOnlyCollection<WorkflowDefinition>)await _workflowDefinitionStore.ListAsync(cancellationToken);

        var reports = new List<WorkflowMigrationReport>(workflows.Count);
        var written = 0;

        foreach (var workflow in workflows)
        {
            var nodeReports = new List<WorkflowNodeMigrationReport>(workflow.Nodes.Count);
            var rewrittenConfiguration = new Dictionary<Guid, string>();

            foreach (var node in workflow.Nodes)
            {
                var (nodeReport, configurationJson) = await PlanNodeAsync(node, cancellationToken);
                nodeReports.Add(nodeReport);

                if (configurationJson is not null)
                {
                    rewrittenConfiguration[node.Id] = configurationJson;
                }
            }

            var report = new WorkflowMigrationReport(workflow.Id, workflow.Name, nodeReports);
            reports.Add(report);

            // All-or-nothing per workflow. A graph where one node still points at a master that has gone is
            // worse half-migrated than untouched: the surviving nodes would read as self-contained while the
            // blocked one silently keeps resolving by id.
            if (dryRun || report.IsBlocked || rewrittenConfiguration.Count == 0)
            {
                continue;
            }

            await WriteAsync(workflow, rewrittenConfiguration, cancellationToken);
            written++;
        }

        return new WorkflowConfigurationMigrationResult(
            dryRun,
            reports,
            written,
            // Orphan transformation rules are reported by a separate pass (plan §7 step 5) — they belong to no
            // workflow and are inert today, so guessing an owner is exactly what caused the problem.
            OrphanTransformationRules: []);
    }

    /// <summary>
    /// Decides what happens to one node, and returns the rewritten JSON when it can be migrated. Returning a
    /// null JSON means "do not write this node" — either there is nothing to do or it is blocked.
    /// </summary>
    private async Task<(WorkflowNodeMigrationReport Report, string? ConfigurationJson)> PlanNodeAsync(
        WorkflowNode node,
        CancellationToken cancellationToken)
    {
        WorkflowNodeMigrationReport Report(
            WorkflowNodeMigrationAction action, IReadOnlyList<string> resolved, string? blocker = null) =>
            new(node.Id, node.NodeType, node.DisplayName, action, resolved, blocker);

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(string.IsNullOrWhiteSpace(node.ConfigurationJson) ? "{}" : node.ConfigurationJson)
                as JsonObject;
        }
        catch (JsonException exception)
        {
            return (Report(WorkflowNodeMigrationAction.Blocked, [], $"Configuration is not valid JSON: {exception.Message}"), null);
        }

        if (root is null)
        {
            return (Report(WorkflowNodeMigrationAction.Blocked, [], "Configuration is not a JSON object."), null);
        }

        if (root[WorkflowNodeConfigurationEnvelope.ConfigProperty] is JsonObject)
        {
            // Re-runnable: an already-enveloped node is left exactly as it is, so a second apply after a partial
            // run is safe and reports honestly rather than double-wrapping.
            return (Report(WorkflowNodeMigrationAction.AlreadyMigrated, []), null);
        }

        var resolved = new List<string>();
        var blockers = new List<string>();

        foreach (var key in MasterIdKeys)
        {
            if (root[key]?.ToString() is not { } raw || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            if (!Guid.TryParse(raw, out var masterId))
            {
                blockers.Add($"{key} '{raw}' is not a valid id.");
                continue;
            }

            var description = await DescribeMasterAsync(key, masterId, cancellationToken);
            if (description is null)
            {
                blockers.Add($"{key} '{masterId}' does not resolve to an existing record.");
                continue;
            }

            resolved.Add(description);
        }

        // Per-resource mapping profile ids, the shape a multi-resource mapping node uses.
        if (root[MappingProfileIdsKey] is JsonObject byResource)
        {
            foreach (var entry in byResource)
            {
                if (entry.Value?.ToString() is not { } raw || !Guid.TryParse(raw, out var profileId))
                {
                    blockers.Add($"{MappingProfileIdsKey}['{entry.Key}'] is not a valid id.");
                    continue;
                }

                var profile = await _configurationRepository.GetMappingProfileAsync(profileId, cancellationToken);
                if (profile is null)
                {
                    blockers.Add($"{MappingProfileIdsKey}['{entry.Key}'] '{profileId}' does not resolve to an existing record.");
                    continue;
                }

                resolved.Add($"mapping profile '{profile.Name}' ({entry.Key})");
            }
        }

        if (blockers.Count > 0)
        {
            return (Report(WorkflowNodeMigrationAction.Blocked, resolved, string.Join(" ", blockers)), null);
        }

        var action = resolved.Count > 0
            ? WorkflowNodeMigrationAction.Migrate
            : WorkflowNodeMigrationAction.EnvelopeOnly;

        return (Report(action, resolved), BuildEnvelope(root));
    }

    /// <summary>
    /// Wraps a node's existing settings in the envelope. The ids stay INSIDE <c>config</c> for now: this phase
    /// only changes the shape, so that readers and the migration itself can be verified against real data while
    /// the old shape still works. Replacing those ids with the resolved settings is phase 5, once dual-read has
    /// been proven — doing both at once would leave no way to tell a shape bug from a resolution bug.
    /// </summary>
    private static string BuildEnvelope(JsonObject root)
    {
        var provenance = new JsonObject
        {
            ["migratedOnUtc"] = DateTime.UtcNow.ToString("O"),
        };

        foreach (var key in MasterIdKeys)
        {
            if (root[key]?.ToString() is { } raw && !string.IsNullOrWhiteSpace(raw))
            {
                provenance[key] = raw;
            }
        }

        return new JsonObject
        {
            [WorkflowNodeConfigurationEnvelope.RefProperty] = provenance,
            [WorkflowNodeConfigurationEnvelope.ConfigProperty] = root.DeepClone(),
        }.ToJsonString();
    }

    /// <summary>How to resolve each key in <see cref="MasterIdKeys"/> — returns a human description of the master
    /// record, or null when the id no longer resolves (which blocks the node).</summary>
    private Task<string?> DescribeMasterAsync(string key, Guid masterId, CancellationToken cancellationToken) => key switch
    {
        SourceConnectionIdKey => DescribeAsync(
            _configurationRepository.GetSourceConnectionAsync(masterId, cancellationToken), source => $"source connection '{source.Name}'"),
        DestinationIdKey => DescribeAsync(
            _configurationRepository.GetDestinationAsync(masterId, cancellationToken), destination => $"destination '{destination.Name}'"),
        MappingProfileIdKey => DescribeAsync(
            _configurationRepository.GetMappingProfileAsync(masterId, cancellationToken), profile => $"mapping profile '{profile.Name}'"),
        _ => throw new ArgumentOutOfRangeException(nameof(key), key, "No resolver is registered for this id key."),
    };

    private static async Task<string?> DescribeAsync<T>(Task<T?> lookup, Func<T, string> describe) where T : class
        => await lookup is { } resolved ? describe(resolved) : null;

    /// <summary>
    /// Persists the rewritten nodes. WorkflowDefinition's nodes are constructor-bound and immutable, so the
    /// graph is rebuilt with the new configuration and saved through the normal store path — which is itself a
    /// delete-and-re-add, and therefore already the shape this rewrite needs.
    /// </summary>
    private async Task WriteAsync(
        WorkflowDefinition workflow,
        IReadOnlyDictionary<Guid, string> rewrittenConfiguration,
        CancellationToken cancellationToken)
    {
        var rebuilt = new WorkflowDefinition(
            workflow.Id, workflow.Name, workflow.Version, workflow.IsEnabled, workflow.IsPubliclyLaunchable, workflow.Description);
        rebuilt.SetTrigger(workflow.Trigger);

        // Node ids are preserved deliberately — edges reference them, and so do WorkflowNodeRuns,
        // FieldLineageEntries and any issued checkpoint url. Minting new ones here would orphan all of that.
        foreach (var node in workflow.Nodes)
        {
            rebuilt.AddNodeWithId(
                node.Id,
                node.NodeType,
                node.Category,
                node.Rank,
                node.SubRank,
                node.DisplayName,
                rewrittenConfiguration.TryGetValue(node.Id, out var configurationJson)
                    ? configurationJson
                    : node.ConfigurationJson,
                node.PositionX,
                node.PositionY,
                node.IsEnabled,
                node.CheckpointUrlEnabled);
        }

        foreach (var edge in workflow.Edges)
        {
            rebuilt.AddEdge(edge.FromNodeId, edge.ToNodeId);
        }

        await _workflowDefinitionStore.SaveAsync(rebuilt, cancellationToken);
    }
}
