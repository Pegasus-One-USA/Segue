using System.Text.Json;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Persistence.Workflows;

/// <summary>
/// SQL-backed <see cref="IWorkflowNodeResourceHistoryRecorder"/>. Persists per-node COUNTS and destination
/// delivery detail only â€” a node's actual output (raw fetched FHIR resources, mapped field values) is never
/// stored, so there is no PHI here to encrypt or decrypt.
/// </summary>
public sealed class EfWorkflowNodeResourceHistoryRecorder : IWorkflowNodeResourceHistoryRecorder
{
    private readonly FHIRBridgeDbContext _dbContext;
    private readonly ILogger<EfWorkflowNodeResourceHistoryRecorder> _logger;

    // No IPhiFieldEncryptor: nothing written here is PHI any more, so there is nothing to encrypt or decrypt.
    public EfWorkflowNodeResourceHistoryRecorder(
        FHIRBridgeDbContext dbContext, ILogger<EfWorkflowNodeResourceHistoryRecorder> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task RecordNodeOutputAsync(
        Guid workflowRunId,
        Guid workflowNodeRunId,
        string nodeType,
        string contract,
        object? payload,
        CancellationToken cancellationToken)
    {
        // Counts are derived HERE, from the live payload, and only the counts are persisted â€” the payload itself
        // is never stored. It previously was (encrypted), which put whole Epic FHIR resources in the database;
        // every screen that read it only needed these numbers.
        //
        // The old `payload is ICollection` test never matched: the contracts are wrapper records
        // (ResourceBatch(IReadOnlyCollection<ResourceEnvelope> Resources), MappedRecordBatch(Records), ...), not
        // collections themselves, so ItemCount silently persisted as NULL on every row and the UI re-derived
        // counts by parsing the stored payload back out. CountItems unwraps the wrapper instead.
        var (itemCount, resourceTypeCounts) = SummarizePayload(payload);

        // A destination node's result is summarised through an explicit ALLOW-LIST, never serialized verbatim.
        // DestinationWriteResult carries the exported file itself (InlineDownload.Content), the written resource
        // ids, and the full outbound email envelope including Subject/Body/To/Cc/AttachmentNames â€” all of which
        // is either PHI outright or routinely carries patient identifiers. Serializing the whole record would
        // have reintroduced, in plaintext, the very content this change exists to stop storing.
        //
        // What survives is the "how much went where" the Execution History card actually renders: counts and
        // status. Anything that could carry a value, a name, an address or a file stays out by construction â€”
        // a new field added to DestinationWriteResult is excluded until someone deliberately maps it here.
        var deliveryDetailJson = string.Equals(contract, "DestinationWriteResult", StringComparison.Ordinal)
            ? SummarizeDelivery(payload)
            : null;

        var record = new WorkflowNodeRunPayload(
            Guid.NewGuid(),
            workflowRunId,
            workflowNodeRunId,
            nodeType,
            contract,
            itemCount,
            resourceTypeCounts is null ? null : JsonSerializer.Serialize(resourceTypeCounts),
            deliveryDetailJson,
            DateTimeOffset.UtcNow);

        await _dbContext.WorkflowNodeRunPayloads.AddAsync(record, cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Counts what a node emitted without retaining any of it: the item total, plus per-resource-type totals when
    /// the payload is a resource batch. Reflection over the wrapper's single collection property keeps this working
    /// for every contract (ResourceBatch.Resources, NormalizedResourceBatch.Resources, MappedRecordBatch.Records)
    /// without this layer having to reference the Runtime payload types or be edited each time one is added.
    ///
    /// Only ResourceType NAMES are read off the items â€” never identifiers or content.
    /// </summary>
    private static (int? ItemCount, SortedDictionary<string, int>? ResourceTypeCounts) SummarizePayload(object? payload)
    {
        if (payload is null)
        {
            return (null, null);
        }

        var items = payload as System.Collections.IEnumerable;
        if (items is null || payload is string)
        {
            // A wrapper record (the normal case): find its single enumerable property and count through that.
            // Matched by NAME, not by reflection order. GetProperties() has no guaranteed ordering, so picking
            // the first enumerable property silently counted the wrong collection on any contract carrying more
            // than one (a Records plus an Errors, say) â€” and which one it picked could differ between runtimes.
            var collectionProperty = payload.GetType()
                .GetProperties()
                .FirstOrDefault(property =>
                    KnownItemCollectionNames.Contains(property.Name)
                    && typeof(System.Collections.IEnumerable).IsAssignableFrom(property.PropertyType)
                    && property.PropertyType != typeof(string));

            items = collectionProperty?.GetValue(payload) as System.Collections.IEnumerable;
        }

        if (items is null)
        {
            // A scalar result (e.g. DestinationWriteResult) â€” RecordsWritten is already persisted on its own row.
            return (null, null);
        }

        var count = 0;
        SortedDictionary<string, int>? byType = null;

        foreach (var item in items)
        {
            count++;
            if (item is null)
            {
                continue;
            }

            var resourceType = item.GetType().GetProperty("ResourceType")?.GetValue(item) as string;
            if (string.IsNullOrWhiteSpace(resourceType))
            {
                continue;
            }

            byType ??= new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            byType[resourceType] = byType.TryGetValue(resourceType, out var current) ? current + 1 : 1;
        }

        return (count, byType);
    }

    /// <summary>The collection property that holds a batch contract's items, by name. See the call site for why
    /// this is matched by name rather than by reflection order.</summary>
    private static readonly HashSet<string> KnownItemCollectionNames =
        new(StringComparer.Ordinal) { "Resources", "Records" };

    /// <summary>
    /// Projects a destination node's result onto the counts-and-status subset that Execution History renders,
    /// dropping everything that carries content: the email Subject and Body, the To/Cc addresses, the attachment
    /// FILE NAMES (routinely "smith-john-labs.csv"), and the signed DownloadUrl â€” a bearer link to the exported
    /// dataset, which is not something to leave sitting in a table the whole Execution History screen reads.
    ///
    /// Written by hand rather than by serializing the record so that this stays an allow-list: a field added to
    /// <c>DestinationWriteResult</c> later is NOT persisted until someone adds it here on purpose.
    /// </summary>
    private static string? SummarizeDelivery(object? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var type = payload.GetType();
        var summary = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["DestinationId"] = type.GetProperty("DestinationId")?.GetValue(payload) as string,
            ["RecordsWritten"] = type.GetProperty("RecordsWritten")?.GetValue(payload) as int?,
            ["WrittenAt"] = type.GetProperty("WrittenAt")?.GetValue(payload),
            // Whether a download exists, never the link itself.
            ["HasDownload"] = type.GetProperty("DownloadUrl")?.GetValue(payload) is string url
                && !string.IsNullOrWhiteSpace(url),
        };

        var email = type.GetProperty("EmailDelivery")?.GetValue(payload);
        if (email is not null)
        {
            var emailType = email.GetType();
            summary["EmailDelivery"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                // Status and Error describe the SEND, not the message, so they are safe and are the whole point
                // of the card: an operator needs to know a delivery was skipped or bounced.
                ["Status"] = emailType.GetProperty("Status")?.GetValue(email) as string,
                ["Error"] = emailType.GetProperty("Error")?.GetValue(email) as string,
                ["ToCount"] = CountOf(emailType.GetProperty("To")?.GetValue(email)),
                ["CcCount"] = CountOf(emailType.GetProperty("Cc")?.GetValue(email)),
                ["AttachmentCount"] = CountOf(emailType.GetProperty("AttachmentNames")?.GetValue(email)),
            };
        }

        return JsonSerializer.Serialize(summary);
    }

    private static int CountOf(object? value)
    {
        if (value is System.Collections.ICollection collection)
        {
            return collection.Count;
        }

        return value is System.Collections.IEnumerable enumerable
            ? enumerable.Cast<object?>().Count()
            : 0;
    }

    public async Task<WorkflowPagedResult<WorkflowNodeRunPayloadDto>> GetPagedAsync(
        Guid workflowRunId,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var query = _dbContext.WorkflowNodeRunPayloads
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId);

        var totalCount = await query.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var records = await query
            .OrderBy(x => x.RecordedAtUtc)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var items = records.Select(record => new WorkflowNodeRunPayloadDto(
            record.Id,
            record.WorkflowNodeRunId,
            record.NodeType,
            record.Contract,
            record.ItemCount,
            record.DeliveryDetailJson,
            record.ResourceTypeCountsJson,
            record.RecordedAtUtc)).ToList();

        return new WorkflowPagedResult<WorkflowNodeRunPayloadDto>(items, totalCount, page, take);
    }

    public async Task<WorkflowPagedResult<WorkflowNodeRunHistoryDto>> GetNodeRunHistoryPagedAsync(
        Guid workflowRunId, int page, int pageSize, CancellationToken cancellationToken)
    {
        var nodeRunsQuery = _dbContext.WorkflowNodeRuns
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId);

        var totalCount = await nodeRunsQuery.CountAsync(cancellationToken);

        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);

        var nodeRuns = await nodeRunsQuery
            .OrderBy(x => x.Rank).ThenBy(x => x.SubRank).ThenBy(x => x.StartedAt)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);

        var nodeRunIds = nodeRuns.Select(x => x.Id).ToList();
        // Projected rather than loading full entities â€” these rows are now metadata-only, but the projection
        // still keeps the query narrow and explicit about what the history list needs.
        var payloadsByNodeRunId = await _dbContext.WorkflowNodeRunPayloads
            .AsNoTracking()
            .Where(p => nodeRunIds.Contains(p.WorkflowNodeRunId))
            .Select(p => new { p.WorkflowNodeRunId, p.Contract, p.ItemCount, p.ResourceTypeCountsJson, p.DeliveryDetailJson, p.RecordedAtUtc })
            .ToListAsync(cancellationToken);

        // A node run can, in principle, have recorded more than one payload â€” take the earliest, matching what
        // GetPagedAsync would surface first for the same node run.
        var payloadByNodeRunId = payloadsByNodeRunId
            .GroupBy(p => p.WorkflowNodeRunId)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.RecordedAtUtc).First());

        var items = nodeRuns.Select(nodeRun =>
        {
            payloadByNodeRunId.TryGetValue(nodeRun.Id, out var payload);
            return new WorkflowNodeRunHistoryDto(
                nodeRun.Id,
                nodeRun.WorkflowNodeId,
                nodeRun.NodeType,
                nodeRun.Rank,
                nodeRun.SubRank,
                nodeRun.Status.ToString(),
                nodeRun.ErrorMessage,
                nodeRun.StartedAt,
                nodeRun.CompletedAt,
                payload?.Contract,
                payload?.ItemCount,
                payload?.ResourceTypeCountsJson,
                payload?.DeliveryDetailJson);
        }).ToList();

        return new WorkflowPagedResult<WorkflowNodeRunHistoryDto>(items, totalCount, page, take);
    }

    public async Task<WorkflowNodeRunPayloadDetailDto?> GetNodeRunPayloadAsync(
        Guid workflowRunId, Guid workflowNodeRunId, CancellationToken cancellationToken)
    {
        var payload = await _dbContext.WorkflowNodeRunPayloads
            .AsNoTracking()
            .Where(p => p.WorkflowRunId == workflowRunId && p.WorkflowNodeRunId == workflowNodeRunId)
            .OrderBy(p => p.RecordedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        return payload is null
            ? null
            : new WorkflowNodeRunPayloadDetailDto(
                workflowNodeRunId, payload.Contract, payload.ItemCount, payload.ResourceTypeCountsJson, payload.DeliveryDetailJson);
    }

    public async Task<WorkflowPagedResult<FieldLineageChainDto>> GetFieldLineagePagedAsync(
        Guid workflowRunId, int page, int pageSize, FieldLineageFilter? filter, CancellationToken cancellationToken)
    {
        var entries = await LoadEntriesAsync(workflowRunId, filter, cancellationToken);

        // Grouped in-memory (not via EF GroupBy translation) so a field's hop chain â€” usually a handful of
        // rows â€” is assembled once per chain rather than split across whatever page boundary the raw rows
        // happened to land on.
        var chains = entries
            .GroupBy(x => (x.ResourceId, x.ResourceType, x.DestinationField, x.SourceField))
            .OrderBy(g => g.Key.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Key.DestinationField, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                return new FieldLineageChainDto(
                    g.Key.ResourceType,
                    g.Key.ResourceId,
                    g.Key.DestinationField,
                    g.Key.SourceField,
                    g.OrderBy(x => x.NodeOrder)
                        .Select(x => new FieldLineageHopDto(
                            x.NodeOrder,
                            x.NodeType,
                            x.ConfigJson,
                            x.Success,
                            x.ErrorMessage,
                            x.DurationMs,
                            x.ExecutedAtUtc))
                        .ToArray(),
                    first.SourceSystemType,
                    first.SourceConnectionName,
                    first.DestinationTypeName,
                    first.DestinationName);
            })
            .ToList();

        var totalCount = chains.Count;
        var take = Math.Clamp(pageSize, 1, 200);
        var skip = Math.Max(0, (page - 1) * take);
        var items = chains.Skip(skip).Take(take).ToList();

        return new WorkflowPagedResult<FieldLineageChainDto>(items, totalCount, page, take);
    }

    public async Task<LineageSummaryDto> GetLineageSummaryAsync(Guid workflowRunId, CancellationToken cancellationToken)
    {
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => new { x.ResourceId, x.DestinationField, x.NodeType, x.Success })
            .ToListAsync(cancellationToken);

        if (entries.Count == 0)
        {
            return new LineageSummaryDto(0, 0, 0, 0);
        }

        var resourcesProcessed = entries.Select(x => x.ResourceId).Distinct().Count();
        var fieldsTransformed = entries.Select(x => x.DestinationField).Distinct().Count();
        var nodesExecuted = entries.Select(x => x.NodeType).Distinct().Count();
        var successRate = entries.Count(x => x.Success) / (double)entries.Count;

        return new LineageSummaryDto(resourcesProcessed, fieldsTransformed, nodesExecuted, successRate);
    }

    /// <summary>The lineage NodeType recorded for a plain field copy, as opposed to a named transformation
    /// rule. Counted separately everywhere below: it is the bulk of every run (2,393 of 2,398 hops in a
    /// typical one) and listing it alongside the real rules would bury them.</summary>
    private const string DirectMappingNodeType = "DirectMapping";

    /// <summary>Matches WorkflowNodeTypes.DeIdentification. Duplicated rather than referenced: this project
    /// sits below the Runtime Application layer that defines it.</summary>
    private const string DeIdentificationNodeType = "DeIdentificationNode";

    public async Task<IReadOnlyDictionary<Guid, NodeLineageBreakdownDto>> GetNodeLineageBreakdownAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        // Projected to just the five columns the breakdown needs before materialising: a busy run records one
        // lineage row per field per resource (2,393 for a single 307-resource run here), and pulling the full
        // entities â€” ConfigJson included â€” to count them would be needlessly heavy.
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => new { x.WorkflowNodeId, x.NodeType, x.ResourceType, x.DestinationField, x.ResourceId, x.Success })
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(x => x.WorkflowNodeId)
            .ToDictionary(
                nodeGroup => nodeGroup.Key,
                nodeGroup => new NodeLineageBreakdownDto(
                    nodeGroup.Key,
                    nodeGroup.Count(),
                    nodeGroup.Select(x => x.DestinationField).Distinct().Count(),
                    nodeGroup.Select(x => x.ResourceId).Distinct().Count(),
                    nodeGroup
                        .GroupBy(x => x.NodeType)
                        .Select(ruleGroup => new LineageRuleCountDto(
                            ruleGroup.Key,
                            ruleGroup.Count(),
                            ruleGroup.Count(x => !x.Success)))
                        // Most-applied first: the headline rule for a mapping node is whichever it ran most,
                        // and a long tail of single-application rules should not push it out of sight.
                        .OrderByDescending(rule => rule.Applications)
                        .ThenBy(rule => rule.NodeType, StringComparer.Ordinal)
                        .ToList(),
                    nodeGroup
                        .GroupBy(x => x.ResourceType)
                        .Select(typeGroup => new LineageResourceTypeCountDto(
                            typeGroup.Key,
                            // Plain field copies are the "mappings" figure; the named rules are broken out
                            // below, so counting them here too would double-count the same hop.
                            typeGroup.Count(x => x.NodeType == DirectMappingNodeType),
                            typeGroup.Select(x => x.ResourceId).Distinct().Count(),
                            typeGroup.Select(x => x.DestinationField).Distinct().Count(),
                            typeGroup
                                .Where(x => x.NodeType != DirectMappingNodeType)
                                .GroupBy(x => x.NodeType)
                                .Select(ruleGroup => new LineageRuleCountDto(
                                    ruleGroup.Key,
                                    ruleGroup.Count(),
                                    ruleGroup.Count(x => !x.Success)))
                                .OrderByDescending(rule => rule.Applications)
                                .ThenBy(rule => rule.NodeType, StringComparer.Ordinal)
                                .ToList()))
                        // Busiest type first, so the bulk of the work leads rather than being ordered by an
                        // alphabetical accident.
                        .OrderByDescending(type => type.Mappings)
                        .ThenBy(type => type.ResourceType, StringComparer.Ordinal)
                        .ToList()));
    }

    public async Task<IReadOnlyList<ConfiguredResourceTypeRulesDto>> GetConfiguredResourceTypeRulesAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        var workflowDefinitionId = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.Id == workflowRunId)
            .Select(x => (Guid?)x.WorkflowDefinitionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (workflowDefinitionId is null)
        {
            return [];
        }

        // ResourcePipelineRouteId carries the WORKFLOW DEFINITION id for V2 rules (they are attached to a
        // workflow, not to a route row), so this is the correct scope â€” without it every workflow's rules
        // would be listed against every run.
        var configuredRules = await _dbContext.TransformationRules
            .AsNoTracking()
            .Where(x => x.ResourcePipelineRouteId == workflowDefinitionId && x.ResourceType != null)
            .Select(x => new { ResourceType = x.ResourceType!, x.NodeType })
            .ToListAsync(cancellationToken);

        // NodeType is an enum on the rule but a string in the lineage this sits alongside; named here so both
        // sides of the screen label the same rule identically.
        var configuredRuleNames = configuredRules
            .Select(x => new { x.ResourceType, NodeType = x.NodeType.ToString() })
            .ToList();

        // Resource types this run actually touched, so a type with no rules configured is still listed (as
        // zero) rather than silently missing â€” "nothing is set up for Observation" is information, and its
        // absence reads as an oversight in the screen rather than in the configuration.
        var resourceTypesInRun = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => x.ResourceType)
            .Distinct()
            .ToListAsync(cancellationToken);

        return ShapeConfiguredRules(
            configuredRuleNames.Select(x => (x.ResourceType, x.NodeType)), resourceTypesInRun);
    }

    public async Task<IReadOnlyList<ConfiguredResourceTypeRulesDto>> GetConfiguredDeIdentificationRulesAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        var profileIds = await ResolveDeIdentificationProfileIdsAsync(workflowRunId, cancellationToken);
        if (profileIds.Count == 0)
        {
            return [];
        }

        // De-identification rules live in the same table as transformation rules but are scoped by profile
        // rather than by workflow, which is why the transformation query above (filtering on
        // ResourcePipelineRouteId) never sees them.
        var deIdRules = await _dbContext.TransformationRules
            .AsNoTracking()
            .Where(x => x.DeIdentificationProfileId != null
                && profileIds.Contains(x.DeIdentificationProfileId.Value)
                && x.ResourceType != null)
            .Select(x => new { ResourceType = x.ResourceType!, x.NodeType })
            .ToListAsync(cancellationToken);

        var resourceTypesInRun = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => x.ResourceType)
            .Distinct()
            .ToListAsync(cancellationToken);

        return ShapeConfiguredRules(
            deIdRules.Select(x => (x.ResourceType, x.NodeType.ToString())), resourceTypesInRun);
    }

    /// <summary>The de-identification profile(s) in play for this run, mirroring the executor's own resolution
    /// order (DeIdentificationNodeExecutor.ResolveProfileIdAsync): a node's explicit profileId first, then the
    /// profile assigned to that node's destination. A set, because a workflow may hold more than one
    /// de-identification node and each can resolve differently.</summary>
    private async Task<IReadOnlyCollection<Guid>> ResolveDeIdentificationProfileIdsAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        var workflowDefinitionId = await _dbContext.WorkflowRuns
            .AsNoTracking()
            .Where(x => x.Id == workflowRunId)
            .Select(x => (Guid?)x.WorkflowDefinitionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (workflowDefinitionId is null)
        {
            return [];
        }

        var deIdNodeConfigurations = await _dbContext.WorkflowNodes
            .AsNoTracking()
            .Where(x => x.WorkflowDefinitionId == workflowDefinitionId.Value
                && x.NodeType == DeIdentificationNodeType)
            .Select(x => x.ConfigurationJson)
            .ToListAsync(cancellationToken);

        var profileIds = new HashSet<Guid>();
        var destinationIds = new HashSet<Guid>();

        foreach (var configurationJson in deIdNodeConfigurations)
        {
            if (string.IsNullOrWhiteSpace(configurationJson))
            {
                continue;
            }

            if (TryReadConfigurationGuid(configurationJson, "profileId", out var explicitProfileId))
            {
                profileIds.Add(explicitProfileId);
            }
            else if (TryReadConfigurationGuid(configurationJson, "destinationId", out var destinationId))
            {
                destinationIds.Add(destinationId);
            }
        }

        if (destinationIds.Count > 0)
        {
            var destinationProfileIds = await _dbContext.DestinationConfigurations
                .AsNoTracking()
                .Where(x => destinationIds.Contains(x.Id) && x.DeIdentificationProfileId != null)
                .Select(x => x.DeIdentificationProfileId!.Value)
                .ToListAsync(cancellationToken);

            foreach (var profileId in destinationProfileIds)
            {
                profileIds.Add(profileId);
            }
        }

        return profileIds;
    }

    private static bool TryReadConfigurationGuid(string configurationJson, string propertyName, out Guid value)
    {
        value = Guid.Empty;
        try
        {
            using var document = JsonDocument.Parse(configurationJson);
            return document.RootElement.TryGetProperty(propertyName, out var property)
                && Guid.TryParse(property.GetString(), out value)
                && value != Guid.Empty;
        }
        catch (JsonException)
        {
            // A node whose configuration will not parse contributes no profile, rather than failing the whole
            // screen over one malformed row.
            return false;
        }
    }

    /// <summary>Groups configured rules by resource type, listing every type the run touched so a type with
    /// nothing configured still appears as zero. "Nothing is set up for Observation" is information, and its
    /// absence would read as a gap in the screen rather than in the configuration.</summary>
    private static IReadOnlyList<ConfiguredResourceTypeRulesDto> ShapeConfiguredRules(
        IEnumerable<(string ResourceType, string NodeType)> configuredRules,
        IEnumerable<string> resourceTypesInRun)
    {
        var rulesByResourceType = configuredRules
            .GroupBy(x => x.ResourceType, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return resourceTypesInRun
            .Union(rulesByResourceType.Keys, StringComparer.OrdinalIgnoreCase)
            .Select(resourceType =>
            {
                var rules = rulesByResourceType.TryGetValue(resourceType, out var matched)
                    ? matched
                        .GroupBy(x => x.NodeType)
                        .Select(g => new ConfiguredRuleCountDto(g.Key, g.Count()))
                        .OrderByDescending(rule => rule.RulesDefined)
                        .ThenBy(rule => rule.NodeType, StringComparer.Ordinal)
                        .ToList()
                    : [];

                return new ConfiguredResourceTypeRulesDto(resourceType, rules.Count, rules);
            })
            // Types with rules lead: a configured type is what the reader came to check, and a tail of
            // zero-rule types should not push it out of view.
            .OrderByDescending(x => x.DistinctRuleTypes)
            .ThenBy(x => x.ResourceType, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<ResourceTypeSummaryDto>> GetLineageResourceTreeAsync(
        Guid workflowRunId, CancellationToken cancellationToken)
    {
        var entries = await _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId)
            .Select(x => new { x.ResourceType, x.ResourceId, x.DestinationField })
            .ToListAsync(cancellationToken);

        return entries
            .GroupBy(x => x.ResourceType, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(resourceGroup => new ResourceTypeSummaryDto(
                resourceGroup.Key,
                resourceGroup.Select(x => x.ResourceId).Distinct().Count(),
                resourceGroup
                    .GroupBy(x => x.DestinationField, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(fieldGroup => fieldGroup.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(fieldGroup => new FieldSummaryDto(
                        fieldGroup.Key,
                        fieldGroup.Select(x => x.ResourceId).Distinct().Count()))
                    .ToArray()))
            .ToArray();
    }

    private async Task<List<FieldLineageEntry>> LoadEntriesAsync(
        Guid workflowRunId, FieldLineageFilter? filter, CancellationToken cancellationToken)
    {
        var query = _dbContext.FieldLineageEntries
            .AsNoTracking()
            .Where(x => x.WorkflowRunId == workflowRunId);

        if (filter is not null)
        {
            if (!string.IsNullOrWhiteSpace(filter.ResourceType))
            {
                query = query.Where(x => x.ResourceType == filter.ResourceType);
            }

            if (!string.IsNullOrWhiteSpace(filter.DestinationField))
            {
                query = query.Where(x => x.DestinationField == filter.DestinationField);
            }

            if (!string.IsNullOrWhiteSpace(filter.ResourceId))
            {
                query = query.Where(x => x.ResourceId == filter.ResourceId);
            }

            if (!string.IsNullOrWhiteSpace(filter.NodeType))
            {
                query = query.Where(x => x.NodeType == filter.NodeType);
            }

            if (!string.IsNullOrWhiteSpace(filter.Search))
            {
                // Values are encrypted at rest and can't be searched in SQL â€” search only spans the plaintext
                // identifying columns (field/node names, resource id), not SourceValueJson/DestinationValueJson.
                var search = filter.Search;
                query = query.Where(x =>
                    x.DestinationField.Contains(search) ||
                    (x.SourceField != null && x.SourceField.Contains(search)) ||
                    x.NodeType.Contains(search) ||
                    x.ResourceId.Contains(search));
            }
        }

        return await query.ToListAsync(cancellationToken);
    }
}
