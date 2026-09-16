using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Workflows;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace FHIRBridge.Infrastructure.Workflows;

/// <summary>
/// Builds the downloadable "entire workflow configuration" text report.
///
/// Two properties of this implementation are deliberate and load-bearing:
///
/// 1. <b>Nothing is hand-listed.</b> Every column printed for a row comes from the EF Core model
///    (<see cref="IEntityType"/>), not from a hard-coded field list — so a column added to any of these tables
///    later shows up in the export automatically instead of silently going missing. The same metadata supplies
///    the real table and column names used to render each section's <c>SELECT</c>.
///
/// 2. <b>Secrets never reach the file.</b> Destination/source credentials already live only in Key Vault (see
///    <c>DestinationConfiguration.ConnectionMetadataJson</c>'s own remarks); what is stored here is a reference.
///    <see cref="IsSecretName"/> is a second, defensive pass over column and JSON property NAMES so that
///    anything secret-shaped is masked even if a future column breaks that rule.
/// </summary>
public sealed class WorkflowConfigurationExporter : IWorkflowConfigurationExporter
{
    private readonly FHIRBridgeDbContext _context;

    public WorkflowConfigurationExporter(FHIRBridgeDbContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Node-configuration JSON keys that hold a reference to another configuration row. The export follows each
    /// of these to pull in the referenced table, which is how the report reaches beyond the three workflow-graph
    /// tables into the source/destination/mapping tables the graph points at.
    /// </summary>
    private static readonly string[] ReferenceKeys =
    [
        "sourceConnectionId",
        "sourceConfigurationId",
        "destinationId",
        "mappingProfileId",
        "mappingProfileIds",
        "deIdentificationProfileId",
        "webhookConfigurationId",
        "resourcePipelineRouteId",
        "ehrEndpointId",
    ];

    /// <summary>Column/JSON-property name fragments whose values are masked rather than printed.</summary>
    private static readonly string[] SecretNameFragments =
    [
        "password", "secret", "privatekey", "clientsecret", "apikey", "accesstoken",
        "refreshtoken", "credential", "connectionstring", "sasToken", "signingkey",
    ];

    private const string RedactedMarker = "«redacted — stored in Key Vault, not exported»";

    public async Task<WorkflowConfigurationExport?> ExportAsync(Guid workflowId, CancellationToken cancellationToken)
    {
        var definition = await _context.WorkflowDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);

        if (definition is null)
        {
            return null;
        }

        var nodes = await _context.WorkflowNodes.AsNoTracking()
            .Where(n => n.WorkflowDefinitionId == workflowId)
            .OrderBy(n => n.Rank).ThenBy(n => n.SubRank)
            .ToListAsync(cancellationToken);

        var edges = await _context.WorkflowEdges.AsNoTracking()
            .Where(e => e.WorkflowDefinitionId == workflowId)
            .ToListAsync(cancellationToken);

        // Every id the graph points at, gathered from the nodes' configuration JSON.
        var referenced = CollectReferencedIds(nodes.Select(n => n.ConfigurationJson));

        var builder = new StringBuilder();
        WriteHeader(builder, definition.Name, workflowId);

        var sections = new List<string>();

        // ── The workflow graph itself ────────────────────────────────────────
        AppendSection(builder, sections, "WorkflowDefinitions",
            $"SELECT * FROM {Table<Runtime.Domain.Workflows.WorkflowDefinition>()} WHERE Id = '{workflowId}';",
            [definition], "The workflow row: name, number, version, enabled state and the flattened trigger columns.");

        AppendSection(builder, sections, "WorkflowNodes",
            $"SELECT * FROM {Table<Runtime.Domain.Workflows.WorkflowNode>()} WHERE WorkflowDefinitionId = '{workflowId}' ORDER BY Rank, SubRank;",
            nodes, "One row per node on the canvas. ConfigurationJson carries the node's own settings and its references to the tables below.");

        AppendSection(builder, sections, "WorkflowEdges",
            $"SELECT * FROM {Table<Runtime.Domain.Workflows.WorkflowEdge>()} WHERE WorkflowDefinitionId = '{workflowId}';",
            edges, "The wiring between nodes (FromNodeId → ToNodeId).");

        // ── Configuration rows the graph references ──────────────────────────
        var sourceConnectionIds = referenced.For("sourceConnectionId");
        var sourceConnections = await LoadAsync(_context.SourceConnections, sourceConnectionIds, cancellationToken);
        AppendSection(builder, sections, "SourceConnections",
            SelectByIds("SourceConnections", sourceConnectionIds), sourceConnections,
            "The EHR/source systems this workflow reads from, with their auth configuration (secret values excluded).");

        var sourceConfigurationIds = referenced.For("sourceConfigurationId");
        var sourceConfigurations = await LoadAsync(_context.SourceConfigurations, sourceConfigurationIds, cancellationToken);
        // Also pull any SourceConfiguration hanging off the connections above, even when no node names it directly.
        var extraSourceConfigurations = await _context.SourceConfigurations.AsNoTracking()
            .Where(c => sourceConnectionIds.Contains(c.ConnectionId))
            .ToListAsync(cancellationToken);
        sourceConfigurations = Merge(sourceConfigurations, extraSourceConfigurations, c => c.Id);
        AppendSection(builder, sections, "SourceConfigurations",
            SelectByIdsOrParent("SourceConfigurations", sourceConfigurationIds, "ConnectionId", sourceConnectionIds),
            sourceConfigurations, "Per-connection retrieval settings (resource types, page sizes, date windows).");

        var capabilityProfiles = await _context.SourceCapabilityProfiles.AsNoTracking()
            .Where(p => sourceConnectionIds.Contains(p.SourceConnectionId))
            .ToListAsync(cancellationToken);
        AppendSection(builder, sections, "SourceCapabilityProfiles",
            SelectByParent("SourceCapabilityProfiles", "SourceConnectionId", sourceConnectionIds),
            capabilityProfiles, "What each source server advertised it supports — drives search-parameter choices at run time.");

        var webhookIds = referenced.For("webhookConfigurationId");
        var webhooks = await LoadAsync(_context.WebhookConfigurations, webhookIds, cancellationToken);
        var extraWebhooks = await _context.WebhookConfigurations.AsNoTracking()
            .Where(w => sourceConnectionIds.Contains(w.SourceConnectionId))
            .ToListAsync(cancellationToken);
        webhooks = Merge(webhooks, extraWebhooks, w => w.Id);
        AppendSection(builder, sections, "WebhookConfigurations",
            SelectByIdsOrParent("WebhookConfigurations", webhookIds, "SourceConnectionId", sourceConnectionIds),
            webhooks, "Inbound webhook endpoints registered against this workflow's sources.");

        var destinationIds = referenced.For("destinationId");
        var destinations = await LoadAsync(_context.DestinationConfigurations, destinationIds, cancellationToken);
        AppendSection(builder, sections, "DestinationConfigurations",
            SelectByIds("DestinationConfigurations", destinationIds), destinations,
            "Where the workflow writes. SecretReference names the Key Vault entry; the secret value itself is never exported.");

        // Mapping profiles: those named by a node, plus any belonging to the destinations above — a profile
        // reached only through the destination would otherwise be missed.
        var mappingProfileIds = referenced.For("mappingProfileId");
        mappingProfileIds.UnionWith(referenced.For("mappingProfileIds"));
        // Tracked (not AsNoTracking) on purpose: the owned MappingFields rows printed below carry their key and
        // owner FK as EF SHADOW properties, whose values are only readable through the change tracker. Loading
        // these two queries untracked would print those columns as null. This is a read-only export — nothing
        // here ever calls SaveChanges — so tracking costs only the identity map.
        var mappingProfiles = await LoadTrackedAsync(_context.MappingProfiles, mappingProfileIds, cancellationToken);
        var extraProfiles = await _context.MappingProfiles
            .Where(p => destinationIds.Contains(p.DestinationId) || sourceConnectionIds.Contains(p.SourceConnectionId))
            .ToListAsync(cancellationToken);
        mappingProfiles = Merge(mappingProfiles, extraProfiles, p => p.Id);
        var allMappingProfileIds = mappingProfiles.Select(p => p.Id).ToHashSet();
        AppendSection(builder, sections, "MappingProfiles",
            SelectByIdsOrParent("MappingProfiles", mappingProfileIds, "DestinationId", destinationIds),
            mappingProfiles, "Field-level mapping definitions (JsonPath → relational field), one per resource type.");

        // MappingFields — the per-field rows owned by each profile above. Its own table, and the part of the
        // configuration an analyst reads most, so it gets a full section of its own.
        AppendOwnedCollectionSections(
            builder, sections, [.. mappingProfiles], _context.Model.FindEntityType(typeof(MappingProfile)),
            "MappingProfiles", allMappingProfileIds);

        // Routes tie a mapping profile to a schedule/trigger; reached through the profiles above.
        var routeIds = referenced.For("resourcePipelineRouteId");
        // Tracked for the same shadow-property reason as the mapping profiles above (ResourcePipelineRouteMappings
        // and its own nested child table are owned collections).
        var routes = await LoadTrackedAsync(_context.ResourcePipelineRoutes, routeIds, cancellationToken);
        var extraRoutes = await _context.ResourcePipelineRoutes
            .Where(r => allMappingProfileIds.Contains(r.MappingProfileId))
            .ToListAsync(cancellationToken);
        routes = Merge(routes, extraRoutes, r => r.Id);
        var allRouteIds = routes.Select(r => r.Id).ToHashSet();
        AppendSection(builder, sections, "ResourcePipelineRoutes",
            SelectByIdsOrParent("ResourcePipelineRoutes", routeIds, "MappingProfileId", allMappingProfileIds),
            routes, "When and how each resource type runs (schedule/cron, webhook, or both).");

        // ResourcePipelineRouteMappings and, nested under those, ...MappingParentReferences — both real tables.
        AppendOwnedCollectionSections(
            builder, sections, [.. routes], _context.Model.FindEntityType(typeof(ResourcePipelineRoute)),
            "ResourcePipelineRoutes", allRouteIds);

        // De-identification profiles: named by a node, or referenced by a destination.
        var deIdIds = referenced.For("deIdentificationProfileId");
        deIdIds.UnionWith(destinations.Where(d => d.DeIdentificationProfileId.HasValue)
            .Select(d => d.DeIdentificationProfileId!.Value));
        var deIdProfiles = await LoadAsync(_context.DeIdentificationProfiles, deIdIds, cancellationToken);
        AppendSection(builder, sections, "DeIdentificationProfiles",
            SelectByIds("DeIdentificationProfiles", deIdIds), deIdProfiles,
            "Governance/de-identification settings applied before write-out.");

        // Transformation rules reach this workflow either through a route or through a de-id profile; global
        // rules (no route, no profile) also apply at run time, so they are included and labelled as such.
        //
        // TransformationRule.ResourcePipelineRouteId is NOT always a ResourcePipelineRoutes FK. A V2-authored
        // rule is bound by TransformationRule.AttachToWorkflow, which stores the WORKFLOW DEFINITION id in that
        // same column (see its doc comment — the resolver matches this id to decide whether a rule applies to a
        // run), and a V2 pipeline has no ResourcePipelineRoutes row at all. Filtering on route ids alone
        // therefore matched nothing for a V2 workflow and silently exported an empty TransformationRules
        // section, even though the rules are live and running. Match this workflow's own id too.
        var ruleOwnerIds = new HashSet<Guid>(allRouteIds) { workflowId };
        var transformationRules = await _context.TransformationRules.AsNoTracking()
            .Where(r => (r.ResourcePipelineRouteId != null && ruleOwnerIds.Contains(r.ResourcePipelineRouteId.Value))
                     || (r.DeIdentificationProfileId != null && deIdIds.Contains(r.DeIdentificationProfileId.Value))
                     || (r.ResourcePipelineRouteId == null && r.DeIdentificationProfileId == null))
            .OrderBy(r => r.Order)
            .ToListAsync(cancellationToken);
        AppendSection(builder, sections, "TransformationRules",
            $"""
             SELECT * FROM {Table<Domain.Entities.TransformationRule>()}
             WHERE ResourcePipelineRouteId IN ({IdList(ruleOwnerIds)})  -- route ids, plus this workflow's own id (V2)
                OR DeIdentificationProfileId IN ({IdList(deIdIds)})
                OR (ResourcePipelineRouteId IS NULL AND DeIdentificationProfileId IS NULL)  -- global rules
             ORDER BY [Order];
             """,
            transformationRules,
            "Per-field transformation rules. Rows with both FK columns NULL are GLOBAL rules — they apply to this workflow too.");

        // SchemaMappings carries no FK: its natural key is (SourceSystem, ResourceType, DestinationTable). Match
        // it on the values this workflow actually uses — the vendor of its source connections, and the resource
        // types / destination objects its own mapping profiles declare.
        var sourceSystems = sourceConnections
            .Select(c => c.SourceSystemType.ToString())
            .Distinct()
            .ToList();
        var resourceTypes = mappingProfiles.Select(p => p.ResourceType)
            .Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList();
        var destinationObjects = mappingProfiles.Select(p => p.DestinationObject)
            .Where(o => !string.IsNullOrWhiteSpace(o)).Distinct().ToList();

        var schemaMappings = sourceSystems.Count == 0 && resourceTypes.Count == 0
            ? []
            : await _context.SchemaMappings.AsNoTracking()
                .Where(m => sourceSystems.Contains(m.SourceSystem)
                         || resourceTypes.Contains(m.ResourceType)
                         || destinationObjects.Contains(m.DestinationTable))
                .ToListAsync(cancellationToken);

        AppendSection(builder, sections, "SchemaMappings",
            $"""
             SELECT * FROM {Table<Domain.Entities.SchemaMapping>()}
             WHERE SourceSystem     IN ({StringList(sourceSystems)})
                OR ResourceType     IN ({StringList(resourceTypes)})
                OR DestinationTable IN ({StringList(destinationObjects)});
             """,
            schemaMappings,
            "Approved schema-discovery mappings matching this workflow's source vendors, resource types and destination objects (this table is keyed by value, not by foreign key).");

        WriteFooter(builder, sections);

        var fileName = $"workflow-config-{Sanitize(definition.Name)}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.txt";
        return new WorkflowConfigurationExport(fileName, builder.ToString());
    }

    // ── Report rendering ────────────────────────────────────────────────────

    private void WriteHeader(StringBuilder builder, string name, Guid workflowId)
    {
        builder.AppendLine("================================================================================");
        builder.AppendLine("FHIRBridge — WORKFLOW CONFIGURATION EXPORT");
        builder.AppendLine("================================================================================");
        builder.AppendLine($"Workflow      : {name}");
        builder.AppendLine($"Workflow Id   : {workflowId}");
        builder.AppendLine($"Generated (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"Database      : {_context.Database.ProviderName}");
        builder.AppendLine();
        builder.AppendLine("Each section below is one configuration table. It shows the SQL query that selects");
        builder.AppendLine("this workflow's rows from that table, followed by every matching row printed one");
        builder.AppendLine("data point per line (Column : Value).");
        builder.AppendLine();
        builder.AppendLine("Secret values (passwords, client secrets, private keys) are NOT exported — they are");
        builder.AppendLine("held in Key Vault and appear here only as the reference that names them.");
        builder.AppendLine();
    }

    private static void WriteFooter(StringBuilder builder, List<string> sections)
    {
        builder.AppendLine("================================================================================");
        builder.AppendLine("END OF EXPORT");
        builder.AppendLine("================================================================================");
        builder.AppendLine($"Tables included ({sections.Count}):");
        foreach (var section in sections)
        {
            builder.AppendLine($"  - {section}");
        }
    }

    /// <summary>
    /// Writes one table section: heading, the SELECT that produced it, then each row point-wise. Columns come
    /// from the EF model, so every mapped column of the entity is printed whether or not it has a value.
    /// </summary>
    private void AppendSection<T>(
        StringBuilder builder,
        List<string> sections,
        string tableLabel,
        string sql,
        IReadOnlyCollection<T> rows,
        string description,
        IEntityType? entityTypeOverride = null)
        where T : class
    {
        sections.Add($"{tableLabel} ({rows.Count} row(s))");

        builder.AppendLine("================================================================================");
        builder.AppendLine($"TABLE: {tableLabel}   —   {rows.Count} row(s)");
        builder.AppendLine("================================================================================");
        builder.AppendLine(description);
        builder.AppendLine();
        builder.AppendLine("-- SQL --------------------------------------------------------------------------");
        builder.AppendLine(sql);
        builder.AppendLine();

        if (rows.Count == 0)
        {
            builder.AppendLine("(no rows — this workflow does not use this table)");
            builder.AppendLine();
            return;
        }

        // An owned child's entity type cannot be found from its CLR type alone (the same type can be owned in
        // more than one place), so callers that already hold it pass it in.
        var entityType = entityTypeOverride ?? _context.Model.FindEntityType(typeof(T));
        var index = 0;

        foreach (var row in rows)
        {
            index++;
            builder.AppendLine($"-- Row {index} of {rows.Count} " + new string('-', Math.Max(1, 60 - index.ToString().Length)));

            foreach (var (column, value) in ReadColumns(entityType, row))
            {
                AppendDataPoint(builder, column, value);
            }

            builder.AppendLine();
        }
    }

    /// <summary>
    /// Every mapped column of <paramref name="row"/>, including the columns of any owned type (the workflow
    /// trigger, a source's authentication block) flattened into the same row with their real column names.
    /// </summary>
    private IEnumerable<(string Column, object? Value)> ReadColumns(IEntityType? entityType, object row)
    {
        if (entityType is null)
        {
            // No EF mapping (should not happen for these types) — fall back to public properties so the section
            // still carries data rather than silently printing nothing.
            foreach (var property in row.GetType().GetProperties().Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
            {
                yield return (property.Name, SafeRead(property, row));
            }
            yield break;
        }

        foreach (var property in entityType.GetProperties())
        {
            var columnName = property.GetColumnName();
            // Shadow properties (an owned collection's synthesized key and its FK back to the owner) have no CLR
            // property or field to read — their value lives only in the change tracker, so ask EF for it rather
            // than printing a misleading "(null)" for a column that is in fact populated.
            var value = property.PropertyInfo is { } info
                ? SafeRead(info, row)
                : property.FieldInfo?.GetValue(row) ?? TryReadShadowValue(row, property);

            // Print what is actually IN the column. Several config columns are JSON-serialized through an EF
            // value converter (SourceConfiguration's per-resource-type sync map, a capability profile's resource
            // list, a pipeline run's resource-type array): the CLR value is a Dictionary/List, but the stored
            // column is a JSON document. Reading only the CLR side would flatten a dictionary to a bare
            // "[key, value]" sequence and lose its structure, so convert first and let AppendDataPoint's JSON
            // expansion render it as the nested data points it really is.
            yield return (string.IsNullOrEmpty(columnName) ? property.Name : columnName, ToColumnValue(property, value));
        }

        // An owned REFERENCE (the workflow trigger, a source's authentication block) is a separate entity type
        // in the model but the same database row, so its columns belong inline here. An owned COLLECTION is a
        // different matter: EF maps it to its own table (MappingProfile.Fields → "MappingFields"), so flattening
        // it into the parent row would both misrepresent the schema and print one arbitrary element's columns.
        // Those get their own section instead — see AppendOwnedCollectionSections.
        foreach (var navigation in entityType.GetNavigations()
                     .Where(n => n.TargetEntityType.IsOwned() && !n.IsCollection))
        {
            var owned = navigation.PropertyInfo is { } info ? SafeRead(info, row) : null;
            if (owned is null)
            {
                continue;
            }

            foreach (var (column, value) in ReadColumns(navigation.TargetEntityType, owned))
            {
                yield return (column, value);
            }
        }
    }

    /// <summary>
    /// Writes a section for each owned COLLECTION of the rows just printed — these are real, separate tables
    /// (e.g. MappingProfile.Fields → "MappingFields"), and the per-field mapping rows they hold are the part an
    /// analyst most needs. Without this they would be missing from the export entirely.
    /// </summary>
    private void AppendOwnedCollectionSections(
        StringBuilder builder,
        List<string> sections,
        IReadOnlyCollection<object> parents,
        IEntityType? parentEntityType,
        string parentTable,
        IEnumerable<Guid> parentIds)
    {
        if (parentEntityType is null || parents.Count == 0)
        {
            return;
        }

        foreach (var navigation in parentEntityType.GetNavigations()
                     .Where(n => n.TargetEntityType.IsOwned() && n.IsCollection))
        {
            var childTable = navigation.TargetEntityType.GetTableName() ?? navigation.Name;
            var foreignKeyColumn = navigation.ForeignKey.Properties[0].GetColumnName();

            var children = new List<object>();
            foreach (var parent in parents)
            {
                if (navigation.PropertyInfo is { } info && SafeRead(info, parent) is System.Collections.IEnumerable items)
                {
                    foreach (var item in items)
                    {
                        children.Add(item);
                    }
                }
            }

            AppendSection(
                builder, sections, childTable,
                $"SELECT * FROM {childTable} WHERE {foreignKeyColumn} IN ({IdList(parentIds)});",
                children,
                $"Rows owned by {parentTable}.",
                navigation.TargetEntityType);

            // Owned collections nest (ResourcePipelineRouteMappings → ...ParentReferences), so recurse rather
            // than stopping at one level — otherwise the innermost table is silently absent from the export.
            var childIds = children
                .Select(child => navigation.TargetEntityType.FindPrimaryKey()?.Properties[0] is { } key
                    && key.PropertyInfo is { } keyInfo
                    && SafeRead(keyInfo, child) is Guid id ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .ToList();

            AppendOwnedCollectionSections(
                builder, sections, children, navigation.TargetEntityType, childTable, childIds);
        }
    }

    /// <summary>
    /// The value as the database column actually stores it: for a property with an EF value converter (a
    /// JSON-serialized dictionary/list, an enum stored as its name), this is the converted provider value.
    /// Returns the CLR value unchanged when there is no converter, and falls back to it if conversion throws.
    /// </summary>
    private static object? ToColumnValue(IProperty property, object? value)
    {
        if (value is null || property.GetValueConverter() is not { } converter)
        {
            return value;
        }

        try
        {
            return converter.ConvertToProvider(value);
        }
        catch (Exception)
        {
            return value;
        }
    }

    /// <summary>
    /// Reads a shadow property's value from the change tracker. Returns null when the instance is not tracked
    /// (the export reads AsNoTracking, but owned-collection children still come back tracked as part of their
    /// owner's graph, which is exactly the case this exists for).
    /// </summary>
    private object? TryReadShadowValue(object row, IProperty property)
    {
        try
        {
            var entry = _context.Entry(row);
            return entry.State == EntityState.Detached ? null : entry.Property(property.Name).CurrentValue;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static object? SafeRead(System.Reflection.PropertyInfo property, object instance)
    {
        try
        {
            return property.GetValue(instance);
        }
        catch (Exception)
        {
            // A computed/derived property that throws must not take the whole export down.
            return "(unavailable)";
        }
    }

    /// <summary>
    /// Prints one data point. JSON-valued columns (ConfigurationJson, ConnectionMetadataJson, ConfigJson) are
    /// expanded into their own indented point-wise lines, since those are exactly the columns an analyst needs
    /// to read and a single unformatted line would be unusable.
    /// </summary>
    private static void AppendDataPoint(StringBuilder builder, string column, object? value)
    {
        if (IsSecretName(column))
        {
            builder.AppendLine($"  {column,-34}: {RedactedMarker}");
            return;
        }

        var text = Stringify(value);

        if (LooksLikeJson(text))
        {
            builder.AppendLine($"  {column,-34}:");
            AppendJson(builder, text!, "      ");
            return;
        }

        builder.AppendLine($"  {column,-34}: {text}");
    }

    private static void AppendJson(StringBuilder builder, string json, string indent)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            builder.AppendLine($"{indent}{json}");
            return;
        }

        AppendJsonNode(builder, node, indent, string.Empty);
    }

    /// <summary>Flattens a JSON document to <c>path : value</c> lines so every nested setting is its own data point.</summary>
    private static void AppendJsonNode(StringBuilder builder, JsonNode? node, string indent, string path)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var entry in obj)
                {
                    var childPath = string.IsNullOrEmpty(path) ? entry.Key : $"{path}.{entry.Key}";
                    if (IsSecretName(entry.Key))
                    {
                        builder.AppendLine($"{indent}{childPath} : {RedactedMarker}");
                        continue;
                    }
                    AppendJsonNode(builder, entry.Value, indent, childPath);
                }
                break;

            case JsonArray array:
                if (array.Count == 0)
                {
                    builder.AppendLine($"{indent}{(string.IsNullOrEmpty(path) ? "(value)" : path)} : (empty list)");
                    break;
                }
                var position = 0;
                foreach (var item in array)
                {
                    // An index is always part of the path, even at the root — an array column (a scopes or
                    // resource-types list) would otherwise print its elements as consecutive bare values with
                    // nothing separating or numbering them.
                    AppendJsonNode(builder, item, indent, $"{path}[{position++}]");
                }
                break;

            default:
                builder.AppendLine($"{indent}{(string.IsNullOrEmpty(path) ? "(value)" : path)} : {node?.ToJsonString() ?? "null"}");
                break;
        }
    }

    private static bool LooksLikeJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var trimmed = text.TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    private static string Stringify(object? value) => value switch
    {
        null => "(null)",
        string s when string.IsNullOrEmpty(s) => "(empty)",
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture),
        byte[] bytes => $"0x{Convert.ToHexString(bytes)}",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        // A collection-valued column (e.g. SourceConnection's Scopes) would otherwise print as its type name
        // ("System.String[]"), which tells the reader nothing about what is actually configured.
        System.Collections.IEnumerable sequence => StringifySequence(sequence),
        _ => value.ToString() ?? "(null)",
    };

    private static string StringifySequence(System.Collections.IEnumerable sequence)
    {
        // Bracketed and comma-separated so a multi-value column stays unambiguous — a bare join reads as one
        // run-on value when the elements themselves contain spaces, and says nothing about how many there are.
        var items = sequence.Cast<object?>().Select(Stringify).ToList();
        return items.Count == 0 ? "(empty list)" : $"[{string.Join(", ", items)}]";
    }

    private static bool IsSecretName(string name) =>
        SecretNameFragments.Any(fragment => name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
        // "SecretReference"/"SecretName"/"SecretKeyVaultName" are POINTERS, not secrets — they name the vault
        // entry and are exactly what makes the export useful for analysis, so they are printed.
        && !name.Contains("SecretReference", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("SecretName", StringComparison.OrdinalIgnoreCase)
        && !name.Contains("KeyVault", StringComparison.OrdinalIgnoreCase);

    // ── Reference collection ────────────────────────────────────────────────

    private sealed class ReferencedIds
    {
        private readonly Dictionary<string, HashSet<Guid>> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public void Add(string key, Guid id)
        {
            if (!_byKey.TryGetValue(key, out var set))
            {
                set = [];
                _byKey[key] = set;
            }
            set.Add(id);
        }

        public HashSet<Guid> For(string key) =>
            _byKey.TryGetValue(key, out var set) ? [.. set] : [];
    }

    /// <summary>
    /// Walks every node's ConfigurationJson (at any nesting depth) and records each GUID found under one of
    /// <see cref="ReferenceKeys"/>. Depth-agnostic on purpose: node config shapes differ per node type, and a
    /// reference nested inside a sub-object must still pull its table into the export.
    /// </summary>
    private static ReferencedIds CollectReferencedIds(IEnumerable<string?> configurationJsons)
    {
        var referenced = new ReferencedIds();

        foreach (var json in configurationJsons)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            JsonNode? root;
            try
            {
                root = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                continue;
            }

            Walk(root, referenced);
        }

        return referenced;
    }

    private static void Walk(JsonNode? node, ReferencedIds referenced)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var entry in obj)
                {
                    var matchedKey = ReferenceKeys.FirstOrDefault(
                        k => string.Equals(k, entry.Key, StringComparison.OrdinalIgnoreCase));

                    if (matchedKey is not null)
                    {
                        // Normalize the plural map form (mappingProfileIds: { Patient: "<guid>", … }) onto the
                        // same bucket as its singular sibling, so callers only ask for one key.
                        var bucket = matchedKey.Equals("mappingProfileIds", StringComparison.OrdinalIgnoreCase)
                            ? "mappingProfileIds"
                            : matchedKey;
                        CollectGuids(entry.Value, bucket, referenced);
                    }

                    Walk(entry.Value, referenced);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, referenced);
                }
                break;
        }
    }

    private static void CollectGuids(JsonNode? node, string key, ReferencedIds referenced)
    {
        switch (node)
        {
            case JsonObject map:
                foreach (var entry in map)
                {
                    CollectGuids(entry.Value, key, referenced);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    CollectGuids(item, key, referenced);
                }
                break;

            case JsonValue value when Guid.TryParse(value.ToString(), out var id) && id != Guid.Empty:
                referenced.Add(key, id);
                break;
        }
    }

    // ── Query helpers ───────────────────────────────────────────────────────

    private Task<List<T>> LoadAsync<T>(DbSet<T> set, HashSet<Guid> ids, CancellationToken cancellationToken)
        where T : class
        => LoadCoreAsync(set, ids, tracked: false, cancellationToken);

    /// <summary>
    /// As <see cref="LoadAsync"/>, but tracked — for entities whose owned collections carry shadow properties
    /// that can only be read through the change tracker. See the call sites for why.
    /// </summary>
    private Task<List<T>> LoadTrackedAsync<T>(DbSet<T> set, HashSet<Guid> ids, CancellationToken cancellationToken)
        where T : class
        => LoadCoreAsync(set, ids, tracked: true, cancellationToken);

    private async Task<List<T>> LoadCoreAsync<T>(DbSet<T> set, HashSet<Guid> ids, bool tracked, CancellationToken cancellationToken)
        where T : class
    {
        if (ids.Count == 0)
        {
            return [];
        }

        // Keyed by the primary key for every one of these tables, so EF's Find-shaped predicate is expressed
        // generically through the model's PK property name.
        var keyName = _context.Model.FindEntityType(typeof(T))?.FindPrimaryKey()?.Properties[0].Name ?? "Id";
        var query = tracked ? set.AsQueryable() : set.AsNoTracking();
        return await query
            .Where(e => ids.Contains(EF.Property<Guid>(e, keyName)))
            .ToListAsync(cancellationToken);
    }

    private static List<T> Merge<T>(List<T> first, List<T> second, Func<T, Guid> keySelector)
    {
        var seen = first.Select(keySelector).ToHashSet();
        foreach (var item in second.Where(item => seen.Add(keySelector(item))))
        {
            first.Add(item);
        }
        return first;
    }

    private string Table<T>() => _context.Model.FindEntityType(typeof(T))?.GetTableName() ?? typeof(T).Name;

    private static string IdList(IEnumerable<Guid> ids)
    {
        var list = ids.ToList();
        return list.Count == 0 ? "NULL" : string.Join(", ", list.Select(id => $"'{id}'"));
    }

    private static string StringList(IEnumerable<string> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? "NULL" : string.Join(", ", list.Select(v => $"'{v.Replace("'", "''")}'"));
    }

    private static string SelectByIds(string table, HashSet<Guid> ids) =>
        $"SELECT * FROM {table} WHERE Id IN ({IdList(ids)});";

    private static string SelectByParent(string table, string parentColumn, IEnumerable<Guid> parentIds) =>
        $"SELECT * FROM {table} WHERE {parentColumn} IN ({IdList(parentIds)});";

    private static string SelectByIdsOrParent(string table, HashSet<Guid> ids, string parentColumn, IEnumerable<Guid> parentIds) =>
        $"SELECT * FROM {table} WHERE Id IN ({IdList(ids)}) OR {parentColumn} IN ({IdList(parentIds)});";

    private static string Sanitize(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        return cleaned.Trim('-') is { Length: > 0 } trimmed ? trimmed : "workflow";
    }
}
