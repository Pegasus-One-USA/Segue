using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Fhir;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Persistence;

/// <summary>
/// See <see cref="IResourceTypeCriteriaBackfill"/>. Workflow runs no longer read a source's own search criteria —
/// neither the source node's "Search criteria" field nor its connection's saved SearchCriteria — only the
/// per-resource-type rows (the Map-fields Criteria button). Without this, a workflow scoped by, say, identifier=…
/// would fetch every patient its source returns on the first run after deploy. For every workflow source node this:
/// <list type="number">
/// <item>Copies the criteria rows of the workflow it was copied from, when the copy has none of its own. Runs used to
/// look rows up by the workflow id stamped into the node's configuration, which a copy kept from its original; they
/// now look them up by the workflow the node belongs to.</item>
/// <item>Moves the old source criteria — the node's value, else its connection's for a parameter the node didn't set —
/// into the rows of the types they used to filter: Patient when the workflow fetches Patient, otherwise each
/// patient-compartment type it fetches (the only searches the old criteria reached). The old criteria win a
/// parameter an existing row also sets, as they did at run time; the row's other parameters are kept.</item>
/// <item>Stamps the node's canvasNodeId (and workflowId) when it has none — rows are keyed by it — with the same value
/// the API would stamp on the workflow's next save.</item>
/// </list>
/// The old values are left in place (they're no longer read), so nothing is lost. All changes land in one SaveChanges,
/// together with the completion marker, so it's all-or-nothing and never runs twice.
/// </summary>
public sealed class ResourceTypeCriteriaBackfill : IResourceTypeCriteriaBackfill
{
    public const string CompletionMarkerKey = "Maintenance:SourceSearchCriteriaMovedToResourceCriteriaOnUtc";

    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ILogger<ResourceTypeCriteriaBackfill> _logger;

    public ResourceTypeCriteriaBackfill(FHIRBridgeDbContext dbContext, ILogger<ResourceTypeCriteriaBackfill> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken cancellationToken)
    {
        if (await _dbContext.SystemSettings.AnyAsync(setting => setting.Key == CompletionMarkerKey, cancellationToken))
        {
            return;
        }

        var nodes = await _dbContext.WorkflowNodes.ToListAsync(cancellationToken);
        var rows = await _dbContext.ResourceTypeCriteria.ToListAsync(cancellationToken);
        var connectionCriteria = (await _dbContext.SourceConnections.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(connection => connection.Id, connection => connection.Retrieval);

        var copiedRows = 0;
        var movedCriteria = 0;
        var stampedNodes = 0;

        foreach (var node in nodes.Where(candidate => candidate.Category == WorkflowNodeCategory.Source))
        {
            if (TryParseObject(node.ConfigurationJson) is not { } root)
            {
                continue;
            }

            var settings = root[WorkflowNodeConfigurationEnvelope.ConfigProperty] as JsonObject ?? root;
            var workflowId = node.WorkflowDefinitionId;
            var canvasNodeId = Read(settings, "canvasNodeId");

            // 1. A pre-PR copy: inherit its original's rows for this node, unless it already has its own.
            if (canvasNodeId is not null
                && Guid.TryParse(Read(settings, "workflowId"), out var stampedWorkflowId)
                && stampedWorkflowId != Guid.Empty
                && stampedWorkflowId != workflowId
                && !rows.Any(row => row.WorkflowId == workflowId && Same(row.SourceNodeId, canvasNodeId)))
            {
                foreach (var original in rows
                    .Where(row => row.WorkflowId == stampedWorkflowId && Same(row.SourceNodeId, canvasNodeId))
                    .Where(row => !string.IsNullOrWhiteSpace(row.Criteria))
                    .ToList())
                {
                    var copy = new ResourceTypeCriteria(workflowId, canvasNodeId, original.ResourceType, original.Criteria);
                    _dbContext.ResourceTypeCriteria.Add(copy);
                    rows.Add(copy);
                    copiedRows++;
                }
            }

            // 2. The old source criteria: the node's own value (its "searchParameters", else its "Search criteria"),
            // then its connection's saved value for any parameter the node didn't set — the resolver's old composition.
            var connectionId = Guid.TryParse(Read(settings, "sourceConnectionId"), out var parsedConnectionId)
                ? parsedConnectionId
                : (Guid?)null;
            var retrieval = connectionId is { } id ? connectionCriteria.GetValueOrDefault(id) : null;
            var oldCriteria = SearchCriteriaPostFilter.CombineCriteria(
                Read(settings, "searchParameters") ?? Read(settings, "Search criteria"),
                retrieval?.SearchCriteria);
            if (oldCriteria is null)
            {
                continue;
            }

            // 3. Rows are keyed by the stamped canvas id — stamp one exactly as the API's StampNodeIdentity would.
            if (canvasNodeId is null)
            {
                canvasNodeId = node.Id.ToString();
                settings["canvasNodeId"] = canvasNodeId;
                if (Read(settings, "workflowId") is null)
                {
                    settings["workflowId"] = workflowId.ToString();
                }

                _dbContext.Entry(node).Property(nameof(WorkflowNode.ConfigurationJson)).CurrentValue = root.ToJsonString();
                stampedNodes++;
            }

            foreach (var resourceType in TargetResourceTypes(nodes, node, settings, retrieval?.ResourceTypes))
            {
                var row = rows.FirstOrDefault(candidate => candidate.WorkflowId == workflowId
                    && Same(candidate.SourceNodeId, canvasNodeId)
                    && string.Equals(candidate.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new ResourceTypeCriteria(workflowId, canvasNodeId, resourceType, oldCriteria);
                    _dbContext.ResourceTypeCriteria.Add(row);
                    rows.Add(row);
                }
                else
                {
                    row.ReplaceCriteria(SearchCriteriaPostFilter.CombineCriteria(oldCriteria, row.Criteria) ?? oldCriteria);
                }

                movedCriteria++;
            }
        }

        _dbContext.SystemSettings.Add(new SystemSetting(
            CompletionMarkerKey,
            DateTime.UtcNow.ToString("O"),
            "Set once source-level Search Criteria were moved into per-resource-type criteria (one-time data migration). "
                + "Leave in place: deleting it re-runs the move on the next start."));
        await _dbContext.SaveChangesAsync(cancellationToken);

        // Counts only: criteria values can carry patient identifiers.
        _logger.LogInformation(
            "Moved source search criteria into {MovedCount} per-resource-type criteria row(s), copied {CopiedCount} row(s) "
            + "to workflows copied from another workflow, and stamped {StampedCount} source node(s).",
            movedCriteria, copiedRows, stampedNodes);
    }

    /// <summary>
    /// The types the node's old source criteria used to filter. They reached only a search still carrying the source's
    /// own parameters: Patient's, or — in a workflow not fetching Patient, so with no patient cohort — each
    /// patient-compartment type's. Cohort-scoped siblings and types outside the compartment never saw them. The
    /// workflow's types are its destinations' selections, else the node's own Resources, else the connection's.
    /// </summary>
    private static IReadOnlyList<string> TargetResourceTypes(
        IReadOnlyCollection<WorkflowNode> nodes, WorkflowNode sourceNode, JsonObject settings, IReadOnlyCollection<string>? connectionTypes)
    {
        var fromDestinations = nodes
            .Where(node => node.WorkflowDefinitionId == sourceNode.WorkflowDefinitionId && node.Category == WorkflowNodeCategory.Destination)
            .SelectMany(node => SplitTypes(TryParseObject(node.ConfigurationJson) is { } root
                ? Read(root[WorkflowNodeConfigurationEnvelope.ConfigProperty] as JsonObject ?? root, "dest_resources")
                : null))
            .ToList();
        var types = fromDestinations.Count > 0
            ? fromDestinations
            : SplitTypes(Read(settings, "Resources")) is { Count: > 0 } nodeTypes
                ? nodeTypes
                : connectionTypes?.ToList() ?? [];

        if (types.Count == 0 || types.Contains("Patient", StringComparer.OrdinalIgnoreCase))
        {
            return ["Patient"];
        }

        return types.Where(PatientCompartmentResourceTypes.IsSupported).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string> SplitTypes(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string? Read(JsonObject settings, string key) =>
        settings[key] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    private static JsonObject? TryParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
