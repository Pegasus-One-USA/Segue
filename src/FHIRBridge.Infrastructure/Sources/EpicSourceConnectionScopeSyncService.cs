using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.Services;
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
        if (sourceConnection is null || sourceConnection.Interactive is null)
        {
            // Backend Services sources have no Interactive configuration and aren't driven by a destination
            // resource picker the same way — leave their scopes exactly as configured.
            return null;
        }

        var workflows = await _workflowStore.ListAsync(cancellationToken);
        var usedResourceTypes = GetUsedResourceTypes(workflows, sourceConnectionId);

        var scopeVersion = DetectScopeVersionFromExistingScopes(sourceConnection.Authentication.Scopes);
        var generated = _scopeGenerator.Generate(
            sourceConnection.ApplicationType,
            usedResourceTypes,
            scopeVersion,
            scopeVersionDetected: false,
            supportedScopes: null);

        if (sourceConnection.Authentication.Scopes.SequenceEqual(generated.Scopes, StringComparer.Ordinal))
        {
            return generated.Scopes;
        }

        var previousScopes = string.Join(' ', sourceConnection.Authentication.Scopes);
        sourceConnection.UpdateScopes([.. generated.Scopes]);
        await _configurationRepository.UpdateSourceConnectionAsync(sourceConnection, cancellationToken);

        _logger.LogInformation(
            "Synced Epic source connection {SourceConnectionId} scopes to match actual pipeline usage. " +
            "Used resource types: [{ResourceTypes}]. Previous scopes: \"{PreviousScopes}\". New scopes: \"{NewScopes}\".",
            sourceConnectionId, string.Join(", ", usedResourceTypes), previousScopes, generated.ScopeString);

        return generated.Scopes;
    }

    public async Task<IReadOnlyList<Guid>> SyncAllAsync(CancellationToken cancellationToken)
    {
        var connections = await _configurationRepository.GetSourceConnectionsAsync(cancellationToken);
        var changed = new List<Guid>();

        foreach (var connection in connections.Where(c => c.Interactive is not null))
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

    private static IEnumerable<string> GetDestinationResourceTypes(WorkflowNode node)
    {
        var raw = TryGetConfigValue(node, DestinationResourcesConfigKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
