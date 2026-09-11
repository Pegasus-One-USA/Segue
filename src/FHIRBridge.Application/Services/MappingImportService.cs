using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Implements the mapping-config import algorithm from <c>MappingImport-API-Spec.md</c> section 3, adapted to
/// the mapping profile/field model that already exists in this codebase (see
/// <see cref="Domain.Entities.MappingProfile"/>).
/// </summary>
public sealed class MappingImportService : IMappingImportService
{
    private static readonly JsonSerializerOptions DeserializeOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IConfigurationRepository _repository;
    private readonly IDestinationSchemaService _destinationSchemaService;
    private readonly IMappingSchemaProviderFactory _schemaProviderFactory;
    private readonly IFhirElementCatalog? _fhirElementCatalog;

    public MappingImportService(
        IConfigurationRepository repository,
        IDestinationSchemaService destinationSchemaService,
        IMappingSchemaProviderFactory schemaProviderFactory,
        IFhirElementCatalog? fhirElementCatalog = null)
    {
        _repository = repository;
        _destinationSchemaService = destinationSchemaService;
        _schemaProviderFactory = schemaProviderFactory;
        _fhirElementCatalog = fhirElementCatalog;
    }

    public async Task<MappingImportResultDto> ImportAsync(JsonElement request, CancellationToken cancellationToken)
    {
        if (!request.TryGetProperty("mappings", out var mappingsElement) || mappingsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("'mappings' is required and must be an array.");
        }

        var rawMappings = mappingsElement.EnumerateArray().Select(e => e.GetRawText()).ToList();
        var importRequest = request.Deserialize<MappingImportRequestDto>(DeserializeOptions)
            ?? throw new InvalidOperationException("Request body could not be parsed.");

        if (importRequest.Mappings.Count == 0)
        {
            throw new InvalidOperationException("'mappings' must contain at least one entry.");
        }

        var items = importRequest.Mappings
            .Zip(rawMappings, (mapping, raw) => (Mapping: mapping, RawJson: raw))
            .OrderBy(x => x.Mapping.Rank)
            .ToList();

        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Mapping.ResourceType))
            {
                throw new InvalidOperationException("Every mapping entry must have a 'resourceType'.");
            }

            if (item.Mapping.Tables is not { Count: > 0 })
            {
                throw new InvalidOperationException($"Mapping entry '{item.Mapping.ResourceType}' must have at least one table.");
            }
        }

        var destination = await _repository.GetDestinationAsync(importRequest.DestinationId, cancellationToken)
            ?? throw new NotFoundException("DestinationConfiguration", importRequest.DestinationId);

        var liveSchema = await _destinationSchemaService.GetSchemaAsync(importRequest.DestinationId, cancellationToken);
        var knownTables = new HashSet<string>(liveSchema.Tables.Select(t => t.TableName), StringComparer.OrdinalIgnoreCase);
        var notNullColumns = BuildNotNullColumnIndex(liveSchema);

        var results = new List<ResourceImportResultDto>();
        foreach (var (mapping, rawJson) in items)
        {
            results.Add(await ImportResourceMappingAsync(
                importRequest, destination, mapping, rawJson, knownTables, notNullColumns, cancellationToken));
        }

        return new MappingImportResultDto(results);
    }

    private async Task<ResourceImportResultDto> ImportResourceMappingAsync(
        MappingImportRequestDto importRequest,
        DestinationConfiguration destination,
        ResourceMappingDto mapping,
        string rawJson,
        HashSet<string> knownTables,
        IReadOnlySet<string> notNullColumns,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        try
        {
            var tablesToCreate = mapping.SchemaChanges?.TablesToCreate ?? [];
            var columnsToAdd = mapping.SchemaChanges?.ColumnsToAdd ?? [];
            var processingOrder = (mapping.ProcessingOrder ?? []).OrderBy(s => s.Step).ToList();
            var destinationObject = ResolveDestinationObject(mapping, processingOrder, warnings);

            ValidateDependsOn(mapping.ResourceType, processingOrder, tablesToCreate, knownTables);

            var provider = _schemaProviderFactory.Create(destination.DestinationType);
            await using var schemaTransaction = await provider.BeginTransactionAsync(destination, cancellationToken);

            var tablesCreated = new List<string>();
            var tablesSkipped = new List<string>();
            var columnsAdded = new List<ColumnAddedDto>();
            var columnsSkipped = new List<ColumnAddedDto>();

            foreach (var step in processingOrder)
            {
                var tableToCreate = tablesToCreate.FirstOrDefault(
                    t => string.Equals(t.Name, step.Table, StringComparison.OrdinalIgnoreCase));

                if (tableToCreate is not null)
                {
                    if (await schemaTransaction.TableExistsAsync(tableToCreate.Name, cancellationToken))
                    {
                        tablesSkipped.Add(tableToCreate.Name);
                    }
                    else
                    {
                        var explicitlyMappedColumns = new HashSet<string>(
                            mapping.Tables
                                .FirstOrDefault(t => string.Equals(t.Name, tableToCreate.Name, StringComparison.OrdinalIgnoreCase))
                                ?.Columns.Select(c => c.Column) ?? [],
                            StringComparer.OrdinalIgnoreCase);
                        await schemaTransaction.CreateTableAsync(tableToCreate, explicitlyMappedColumns, cancellationToken);
                        tablesCreated.Add(tableToCreate.Name);
                    }

                    knownTables.Add(tableToCreate.Name);
                    continue;
                }

                foreach (var column in columnsToAdd.Where(
                    c => string.Equals(c.Table, step.Table, StringComparison.OrdinalIgnoreCase)))
                {
                    if (await schemaTransaction.ColumnExistsAsync(step.Table, column.Name, cancellationToken))
                    {
                        columnsSkipped.Add(new ColumnAddedDto(step.Table, column.Name));
                    }
                    else
                    {
                        await schemaTransaction.AddColumnAsync(step.Table, column, cancellationToken);
                        columnsAdded.Add(new ColumnAddedDto(step.Table, column.Name));
                    }
                }

                knownTables.Add(step.Table);
            }

            var fields = new List<MappingField>();
            var relationLookupCache = new Dictionary<string, TableRelationDto?>(StringComparer.OrdinalIgnoreCase);
            foreach (var table in mapping.Tables)
            {
                foreach (var column in table.Columns ?? [])
                {
                    fields.Add(await BuildFieldAsync(
                        schemaTransaction, mapping, table, column, destinationObject, tablesToCreate, columnsToAdd,
                        notNullColumns, warnings, relationLookupCache, cancellationToken));
                }
            }

            // Resolve strictly by the id the caller already knows (round-tripped from a prior import of this
            // exact node/resource) — never by searching for "the" profile matching (ResourceType,
            // SourceConnectionId, DestinationId). That triple is shared by any workflow built on the same
            // source connection + destination + resource type, so searching on it would silently find and
            // overwrite a DIFFERENT workflow's profile (the "Invalid column name" incident this replaces).
            // No id means a genuinely first-ever save for this node/resource — always create a new profile
            // rather than adopting one that happens to match the triple.
            var existing = mapping.ExistingMappingProfileId is { } existingProfileId
                ? await _repository.GetMappingProfileAsync(existingProfileId, cancellationToken)
                : null;

            Guid profileId;
            if (existing is not null)
            {
                existing.Update(
                    mapping.ResourceType,
                    mapping.ResourceType,
                    importRequest.SourceConnectionId,
                    importRequest.DestinationId,
                    destinationObject,
                    fields);
                existing.SetMappingJson(rawJson);
                await _repository.UpdateMappingProfileAsync(existing, cancellationToken);
                profileId = existing.Id;
            }
            else
            {
                var profile = new MappingProfile(
                    mapping.ResourceType,
                    mapping.ResourceType,
                    importRequest.SourceConnectionId,
                    importRequest.DestinationId,
                    destinationObject,
                    fields,
                    mappingJson: rawJson);
                await _repository.AddMappingProfileAsync(profile, cancellationToken);
                profileId = profile.Id;
            }

            // Commit the destination schema changes only now that the control-plane write has also succeeded —
            // if anything above throws (a DDL step, or the MappingProfile/Fields save), this schema transaction
            // is disposed without ever being committed, so every table/column created for this resourceType so
            // far rolls back too, rather than being left stranded with no profile referencing it.
            await schemaTransaction.CommitAsync(cancellationToken);

            return new ResourceImportResultDto(
                mapping.ResourceType, profileId, tablesCreated, tablesSkipped, columnsAdded, columnsSkipped,
                fields.Count, warnings);
        }
        catch (Exception exception)
        {
            warnings.Add($"Import failed: {exception.Message}");
            return new ResourceImportResultDto(mapping.ResourceType, Guid.Empty, [], [], [], [], 0, warnings);
        }
    }

    private static void ValidateDependsOn(
        string resourceType,
        IReadOnlyList<ProcessingOrderStepDto> processingOrder,
        IReadOnlyList<TableDefinitionDto> tablesToCreate,
        HashSet<string> knownTables)
    {
        foreach (var step in processingOrder)
        {
            if (step.DependsOn is null)
            {
                continue;
            }

            var known = knownTables.Contains(step.DependsOn)
                || tablesToCreate.Any(t => string.Equals(t.Name, step.DependsOn, StringComparison.OrdinalIgnoreCase));

            if (!known)
            {
                throw new InvalidOperationException(
                    $"'{resourceType}': processingOrder step '{step.Table}' depends on unknown table '{step.DependsOn}'.");
            }
        }
    }

    /// <summary>
    /// A level-1, no-dependency processingOrder step is, by construction, this resourceType's own root table —
    /// whatever the user actually named it (e.g. a renamed "PatientV2" to avoid colliding with another mapping
    /// profile sharing the same destination). Matching on the table NAME too (requiring it to literally equal
    /// the resourceType) would silently undo any such rename and collapse every renamed root table back onto
    /// the bare resourceType name — exactly the bug this method must not have.
    /// </summary>
    private static string ResolveDestinationObject(
        ResourceMappingDto mapping, IReadOnlyList<ProcessingOrderStepDto> processingOrder, List<string> warnings)
    {
        var rootStep = processingOrder.FirstOrDefault(s => s.Level == 1 && s.DependsOn is null);

        if (rootStep is not null)
        {
            return rootStep.Table;
        }

        warnings.Add(
            $"No level-1 processingOrder step was found for '{mapping.ResourceType}'; " +
            $"defaulting DestinationObject to the resourceType name.");
        return mapping.ResourceType;
    }

    private async Task<MappingField> BuildFieldAsync(
        IMappingSchemaTransaction schemaTransaction,
        ResourceMappingDto mapping,
        TargetTableDto table,
        ColumnMappingDto column,
        string destinationObject,
        IReadOnlyList<TableDefinitionDto> tablesToCreate,
        IReadOnlyList<ColumnToAddDto> columnsToAdd,
        IReadOnlySet<string> notNullColumns,
        List<string> warnings,
        Dictionary<string, TableRelationDto?> relationLookupCache,
        CancellationToken cancellationToken)
    {
        var (jsonPath, format) = BuildJsonPathAndFormat(column, mapping.ResourceType);
        var dataType = await ResolveDataTypeAsync(
            schemaTransaction, table.Name, column.Column, tablesToCreate, columnsToAdd, cancellationToken);
        var valueType = MapValueType(dataType);
        var effectiveRelation = await ResolveEffectiveRelationAsync(
            schemaTransaction, table, destinationObject, relationLookupCache, cancellationToken);
        var (arrayPolicy, cardinality, arrayAncestors) = ResolveArrayMetadata(
            column.Instance, effectiveRelation, table.Name, destinationObject, mapping.ResourceType, warnings);

        return new MappingField(
            TargetField: column.Column,
            JsonPath: jsonPath,
            ValueType: valueType,
            // A column the destination declares NOT NULL is required by definition — nothing else can supply
            // its value once a mapping owns it. Matters because CreateMappingProfileRequestValidator gates the
            // OTHER path that writes the same profiles (the workflow save) on exactly this, so leaving it
            // hardcoded false here made the two paths disagree about the same field. Tables/columns this import
            // is about to create are always created nullable, so they're correctly absent from this index.
            IsRequired: notNullColumns.Contains(NotNullColumnKey(table.Name, column.Column)),
            DefaultValue: null,
            Format: format,
            ResourceType: mapping.ResourceType,
            DestinationObject: table.Name,
            NormalizationType: null,
            TerminologySystemJsonPath: null,
            TerminologyCodeJsonPath: null,
            IsEnabled: true,
            ArrayPolicy: arrayPolicy,
            Cardinality: cardinality,
            ArrayAncestors: arrayAncestors,
            ParentTable: effectiveRelation?.ParentTable,
            ParentKeyColumn: effectiveRelation?.ParentColumn,
            ForeignKeyColumn: effectiveRelation?.ChildColumn,
            ReferenceLookupTable: column.ReferenceLookup?.Table,
            ReferenceLookupKeyColumn: column.ReferenceLookup?.KeyColumn);
    }

    /// <summary>
    /// Prefers the payload's own declared <c>table.Relation</c>; when that's missing for a genuinely separate
    /// (non-root) table, falls back to the table's real FK constraint already in the destination — e.g. a
    /// child table an earlier import created correctly, but whose relation the current payload simply omits.
    /// Cached per table name since every column of the same table would otherwise repeat the same lookup.
    /// </summary>
    private static async Task<TableRelationDto?> ResolveEffectiveRelationAsync(
        IMappingSchemaTransaction schemaTransaction,
        TargetTableDto table,
        string destinationObject,
        Dictionary<string, TableRelationDto?> relationLookupCache,
        CancellationToken cancellationToken)
    {
        if (table.Relation is not null)
        {
            return table.Relation;
        }

        if (string.Equals(table.Name, destinationObject, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (relationLookupCache.TryGetValue(table.Name, out var cached))
        {
            return cached;
        }

        var discovered = await schemaTransaction.GetForeignKeyAsync(table.Name, cancellationToken);
        relationLookupCache[table.Name] = discovered;
        return discovered;
    }

    /// <summary>
    /// Builds a JsonPath the hand-rolled <see cref="JsonMappingEngine"/> can actually resolve: strips the
    /// redundant leading "{ResourceType}." segment the UI's dotted paths carry (the engine already scopes to
    /// this one resource's own JSON), then — when the column has an <see cref="InstanceSelectorDto.ArrayContext"/>
    /// — splices <c>[*]</c> onto every segment the array context spans, since a plain segment name against a
    /// JSON array returns the array itself rather than fanning out over its items (see
    /// <c>JsonMappingEngine.ResolveAll</c>). A multi-segment array context (e.g. a repeating field nested inside
    /// another repeating element, such as "contact.relationship") gets a wildcard on each of its segments.
    /// </summary>
    private (string JsonPath, string Format) BuildJsonPathAndFormat(ColumnMappingDto column, string resourceType)
    {
        var aggregate = column.Instance?.Aggregate;
        var aggregateSuffix = !string.IsNullOrWhiteSpace(aggregate) && !string.Equals(aggregate, "rows", StringComparison.OrdinalIgnoreCase)
            ? $";aggregate={aggregate}"
            : string.Empty;
        var arrayContext = column.Instance?.ArrayContext;

        return column.Mode switch
        {
            "directField" => (
                BuildResolvableJsonPath(
                    column.Sources?.FirstOrDefault()
                        ?? throw new InvalidOperationException($"Column '{column.Column}' (directField) has no source."),
                    resourceType, arrayContext),
                $"directField{aggregateSuffix}"),

            "joinedFields" => (
                string.Join('|', (column.Sources ?? []).Select(source => BuildResolvableJsonPath(source, resourceType, arrayContext))),
                $"joinedFields;delimiter={column.Delimiter}{aggregateSuffix}"),

            "wholeNodeAsJson" => (
                BuildResolvableJsonPath(
                    column.SourceNode
                        ?? throw new InvalidOperationException($"Column '{column.Column}' (wholeNodeAsJson) has no sourceNode."),
                    resourceType, arrayContext),
                $"wholeNodeAsJson{aggregateSuffix}"),

            _ => throw new InvalidOperationException($"Unknown column mode '{column.Mode}' for column '{column.Column}'.")
        };
    }

    private string BuildResolvableJsonPath(string rawPath, string resourceType, string? arrayContext)
    {
        var strippedPath = StripResourceTypePrefix(rawPath, resourceType);

        // A wholeNodeAsJson column whose sourceNode is the resource's own ROOT node (the whole payload, e.g.
        // "Patient") strips to nothing meaningful — the tree's root node id is just the resourceType itself.
        // Only "$" means "the whole document" to JsonMappingEngine.ResolveAll; falling through below would
        // build "$.Patient", which resolves to nothing and writes NULL into the target column on every record.
        if (string.IsNullOrWhiteSpace(strippedPath)
            || string.Equals(strippedPath, resourceType, StringComparison.OrdinalIgnoreCase))
        {
            return "$";
        }

        // Prefer the FHIR element catalog's own pre-computed JsonPath when this exact fhirPath is a known,
        // real element: it already knows precisely which ancestor segment(s) are genuinely repeating arrays
        // versus merely a non-repeating object on the way to one (e.g. Condition.code.coding.code — only
        // "coding" repeats, "code" itself is a single 0..1 CodeableConcept) — a distinction the segment-count
        // heuristic below can't make from arrayContext's dotted string alone, and got wrong in production
        // (stored as "$.code[*].coding[*].code", wildcarding "code" too, which silently resolved to nothing).
        var catalogMatch = _fhirElementCatalog?.Fields(resourceType)
            .FirstOrDefault(f => string.Equals(f.FhirPath, strippedPath, StringComparison.OrdinalIgnoreCase));
        if (catalogMatch is not null)
        {
            return catalogMatch.JsonPath;
        }

        if (string.IsNullOrWhiteSpace(arrayContext))
        {
            return $"$.{strippedPath}";
        }

        var contextSegments = StripResourceTypePrefix(arrayContext, resourceType)
            .Split('.', StringSplitOptions.RemoveEmptyEntries);
        var pathSegments = strippedPath.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (contextSegments.Length == 0 || contextSegments.Length > pathSegments.Length)
        {
            return $"$.{strippedPath}";
        }

        for (var i = 0; i < contextSegments.Length; i++)
        {
            if (!pathSegments[i].Equals(contextSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                // arrayContext isn't actually a prefix of this path (unexpected shape) — fall back, no fan-out.
                return $"$.{strippedPath}";
            }

            pathSegments[i] += "[*]";
        }

        return "$." + string.Join('.', pathSegments);
    }

    /// <summary>
    /// "table.column" keys for every live column the destination declares NOT NULL, indexed under both the
    /// qualified ("public.Patient") and bare ("Patient") table name since a mapping payload can name either.
    /// Auto-generated columns are excluded: the database supplies their value, so a mapping can't be required
    /// to (and BuildFieldAsync's caller never maps one anyway).
    /// </summary>
    private static IReadOnlySet<string> BuildNotNullColumnIndex(DestinationSchemaDto schema)
    {
        var index = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in schema.Tables)
        {
            foreach (var column in table.Columns.Where(c => !c.IsNullable && !c.IsAutoGenerated))
            {
                index.Add(NotNullColumnKey(table.FullName, column.Name));
                index.Add(NotNullColumnKey(table.TableName, column.Name));
            }
        }

        return index;
    }

    private static string NotNullColumnKey(string tableName, string columnName) => $"{tableName}.{columnName}";

    private static string StripResourceTypePrefix(string path, string resourceType)
    {
        var prefix = resourceType + ".";
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? path[prefix.Length..] : path;
    }

    /// <summary>
    /// Resolves a column's raw destination data type: a newly-created table's own column definition, then a
    /// column being added to an existing table, then (for a column on an already-existing table/column) the
    /// real type read live from the destination. Defaults to <c>nvarchar</c> if none of those resolve it —
    /// e.g. a column mapped onto a pre-existing table/column the mapping doesn't otherwise describe.
    /// </summary>
    private static async Task<string> ResolveDataTypeAsync(
        IMappingSchemaTransaction schemaTransaction,
        string tableName,
        string columnName,
        IReadOnlyList<TableDefinitionDto> tablesToCreate,
        IReadOnlyList<ColumnToAddDto> columnsToAdd,
        CancellationToken cancellationToken)
    {
        var newTableColumn = tablesToCreate
            .FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase))
            ?.Columns.FirstOrDefault(c => string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (newTableColumn is not null)
        {
            return newTableColumn.DataType;
        }

        var addedColumn = columnsToAdd.FirstOrDefault(
            c => string.Equals(c.Table, tableName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(c.Name, columnName, StringComparison.OrdinalIgnoreCase));
        if (addedColumn is not null)
        {
            return addedColumn.DataType;
        }

        var liveType = await schemaTransaction.GetColumnDataTypeAsync(tableName, columnName, cancellationToken);
        return liveType ?? "nvarchar(200)";
    }

    private static MappingValueType MapValueType(string dataType)
    {
        var family = dataType.Split('(')[0].Trim().ToLowerInvariant();
        return family switch
        {
            "bit" => MappingValueType.Boolean,
            "tinyint" or "smallint" or "int" or "bigint" => MappingValueType.Integer,
            "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => MappingValueType.Decimal,
            "date" => MappingValueType.Date,
            "datetime" or "datetime2" or "datetimeoffset" or "smalldatetime" => MappingValueType.DateTime,
            _ => MappingValueType.String
        };
    }

    /// <summary>
    /// Maps the JSON's "which elements were selected" (<see cref="InstanceSelectorDto.Type"/>) onto the
    /// domain's "how is a repeating element materialized" <see cref="ArrayPolicy"/> — a deliberate, documented
    /// judgment call (see the import-API plan) since the two aren't the same axis. "aggregate":"csv" (joining
    /// a nested array into one delimited string) doesn't fit any existing ArrayPolicy value and is instead
    /// encoded onto <see cref="MappingField.Format"/> by <see cref="BuildJsonPathAndFormat"/>.
    /// </summary>
    /// <remarks>
    /// The instance selection ("first"/"all"/etc.) only has meaning on the profile's own root table — it picks
    /// which occurrence(s) of a repeating element land in THAT SAME table's row(s) (<see cref="ArrayPolicy.FirstItem"/>
    /// or <see cref="ArrayPolicy.RepeatParent"/>). A genuinely separate destination table exists specifically so
    /// each occurrence becomes its own row there, so it must always resolve to <see cref="ArrayPolicy.SeparateDestination"/>
    /// regardless of which instance type was selected — checking <paramref name="tableName"/> against
    /// <paramref name="destinationObject"/> must happen BEFORE inspecting <see cref="InstanceSelectorDto.Type"/>,
    /// or "first" (the payload's default selection) would wrongly collapse a child table's repeating rows into a
    /// single row merged onto the parent table instead. A separate table always needs <paramref name="tableRelation"/>
    /// to link its rows back to a parent; if one is missing, that's a payload/spec problem surfaced via
    /// <paramref name="warnings"/> rather than a silent misclassification onto the root table.
    /// </remarks>
    private static (ArrayPolicy Policy, string? Cardinality, string? ArrayAncestors) ResolveArrayMetadata(
        InstanceSelectorDto? instance,
        TableRelationDto? tableRelation,
        string tableName,
        string destinationObject,
        string resourceType,
        List<string> warnings)
    {
        if (instance is null)
        {
            return (ArrayPolicy.Scalar, null, null);
        }

        var isRootTable = string.Equals(tableName, destinationObject, StringComparison.OrdinalIgnoreCase);
        if (!isRootTable)
        {
            if (tableRelation is null)
            {
                warnings.Add(
                    $"'{resourceType}': table '{tableName}' has repeating field(s) but no declared relation back to " +
                    $"root table '{destinationObject}' — its rows cannot be linked to a parent row.");
            }

            return (ArrayPolicy.SeparateDestination, "OneToMany", instance.ArrayContext);
        }

        if (string.Equals(instance.Type, "first", StringComparison.OrdinalIgnoreCase))
        {
            return (ArrayPolicy.FirstItem, "OneToMany", instance.ArrayContext);
        }

        return (ArrayPolicy.RepeatParent, "OneToMany", instance.ArrayContext);
    }
}
