using System.Text.Json;
using FHIRBridge.Application.Abstractions.Destinations;
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

    public MappingImportService(
        IConfigurationRepository repository,
        IDestinationSchemaService destinationSchemaService,
        IMappingSchemaProviderFactory schemaProviderFactory)
    {
        _repository = repository;
        _destinationSchemaService = destinationSchemaService;
        _schemaProviderFactory = schemaProviderFactory;
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

        var results = new List<ResourceImportResultDto>();
        foreach (var (mapping, rawJson) in items)
        {
            results.Add(await ImportResourceMappingAsync(
                importRequest, destination, mapping, rawJson, knownTables, cancellationToken));
        }

        return new MappingImportResultDto(results);
    }

    private async Task<ResourceImportResultDto> ImportResourceMappingAsync(
        MappingImportRequestDto importRequest,
        DestinationConfiguration destination,
        ResourceMappingDto mapping,
        string rawJson,
        HashSet<string> knownTables,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();

        try
        {
            var tablesToCreate = mapping.SchemaChanges?.TablesToCreate ?? [];
            var columnsToAdd = mapping.SchemaChanges?.ColumnsToAdd ?? [];
            var processingOrder = (mapping.ProcessingOrder ?? []).OrderBy(s => s.Step).ToList();

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
                        await schemaTransaction.CreateTableAsync(tableToCreate, cancellationToken);
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
            foreach (var table in mapping.Tables)
            {
                foreach (var column in table.Columns ?? [])
                {
                    fields.Add(await BuildFieldAsync(
                        schemaTransaction, mapping, table, column, tablesToCreate, columnsToAdd, cancellationToken));
                }
            }

            var destinationObject = ResolveDestinationObject(mapping, processingOrder, warnings);

            var existing = await _repository.FindMappingProfileAsync(
                mapping.ResourceType, importRequest.SourceConnectionId, importRequest.DestinationId, cancellationToken);

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
                    rawJson);
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

    private static string ResolveDestinationObject(
        ResourceMappingDto mapping, IReadOnlyList<ProcessingOrderStepDto> processingOrder, List<string> warnings)
    {
        var rootStep = processingOrder.FirstOrDefault(
            s => s.Level == 1 && s.DependsOn is null
                && string.Equals(s.Table, mapping.ResourceType, StringComparison.OrdinalIgnoreCase));

        if (rootStep is not null)
        {
            return rootStep.Table;
        }

        warnings.Add(
            $"No level-1 processingOrder step named '{mapping.ResourceType}' was found; " +
            $"defaulting DestinationObject to the resourceType name.");
        return mapping.ResourceType;
    }

    private async Task<MappingField> BuildFieldAsync(
        IMappingSchemaTransaction schemaTransaction,
        ResourceMappingDto mapping,
        TargetTableDto table,
        ColumnMappingDto column,
        IReadOnlyList<TableDefinitionDto> tablesToCreate,
        IReadOnlyList<ColumnToAddDto> columnsToAdd,
        CancellationToken cancellationToken)
    {
        var (jsonPath, format) = BuildJsonPathAndFormat(column);
        var dataType = await ResolveDataTypeAsync(
            schemaTransaction, table.Name, column.Column, tablesToCreate, columnsToAdd, cancellationToken);
        var valueType = MapValueType(dataType);
        var (arrayPolicy, cardinality, arrayAncestors) = ResolveArrayMetadata(column.Instance, table.Relation);

        return new MappingField(
            TargetField: column.Column,
            JsonPath: jsonPath,
            ValueType: valueType,
            IsRequired: false,
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
            ArrayAncestors: arrayAncestors);
    }

    private static (string JsonPath, string Format) BuildJsonPathAndFormat(ColumnMappingDto column)
    {
        var aggregate = column.Instance?.Aggregate;
        var aggregateSuffix = !string.IsNullOrWhiteSpace(aggregate) && !string.Equals(aggregate, "rows", StringComparison.OrdinalIgnoreCase)
            ? $";aggregate={aggregate}"
            : string.Empty;

        return column.Mode switch
        {
            "directField" => (
                column.Sources?.FirstOrDefault()
                    ?? throw new InvalidOperationException($"Column '{column.Column}' (directField) has no source."),
                $"directField{aggregateSuffix}"),

            "joinedFields" => (
                string.Join('|', column.Sources ?? []),
                $"joinedFields;delimiter={column.Delimiter}{aggregateSuffix}"),

            "wholeNodeAsJson" => (
                column.SourceNode
                    ?? throw new InvalidOperationException($"Column '{column.Column}' (wholeNodeAsJson) has no sourceNode."),
                $"wholeNodeAsJson{aggregateSuffix}"),

            _ => throw new InvalidOperationException($"Unknown column mode '{column.Mode}' for column '{column.Column}'.")
        };
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
    private static (ArrayPolicy Policy, string? Cardinality, string? ArrayAncestors) ResolveArrayMetadata(
        InstanceSelectorDto? instance, TableRelationDto? tableRelation)
    {
        if (instance is null)
        {
            return (ArrayPolicy.Scalar, null, null);
        }

        var policy = string.Equals(instance.Type, "first", StringComparison.OrdinalIgnoreCase)
            ? ArrayPolicy.FirstItem
            : tableRelation is not null
                ? ArrayPolicy.SeparateDestination
                : ArrayPolicy.RepeatParent;

        return (policy, "OneToMany", instance.ArrayContext);
    }
}
