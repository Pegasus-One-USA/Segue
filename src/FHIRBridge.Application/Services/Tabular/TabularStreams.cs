using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Tabular;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>
/// One resource type a Tabular source reads, from its own query (SQL) or file (CSV), built by its own template. A
/// query or file feeds only its own template, so every type can use plain column names (each query its own
/// <c>patient_id</c>) instead of one wide table shared by all of them.
/// </summary>
/// <param name="Query">SQL sources: the SELECT that returns this type's rows.</param>
/// <param name="FileId">CSV sources: the uploaded file; several types may read the same file.</param>
/// <param name="RowFilterColumn">CSV sources, optional: only rows whose value in this column equals
/// <paramref name="RowFilterValue"/> (case and surrounding spaces ignored), for a file that holds several types.</param>
public sealed record TabularStream(
    string ResourceType,
    TabularFhirTemplate Template,
    string? Query,
    Guid? FileId,
    string? RowFilterColumn,
    string? RowFilterValue);

/// <summary>
/// Reads and checks the per-type entries a Tabular source stores under <see cref="TabularSourceSettings.StreamsKey"/>:
/// a JSON array of <c>{resourceType, query | fileId, rowFilterColumn?, rowFilterValue?, template}</c>. A node without
/// it is an older single-query node and still runs through <see cref="TabularSourceSettings.TemplatesKey"/>.
/// </summary>
public static class TabularStreams
{
    public static IReadOnlyList<TabularStream> Parse(string? json, string? kind)
    {
        JsonArray? array;
        try
        {
            array = string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            throw new BusinessRuleException("The resource types to read are not valid JSON.");
        }

        if (array is null || array.Count == 0)
        {
            throw new BusinessRuleException("Choose at least one resource type to read.");
        }

        if (array.Count > TabularSourceSettings.MaxStreams)
        {
            throw new BusinessRuleException($"At most {TabularSourceSettings.MaxStreams} resource types can be read by one source.");
        }

        var csv = kind == TabularSourceSettings.CsvKind;
        var streams = new List<TabularStream>();
        for (var i = 0; i < array.Count; i++)
        {
            var entry = array[i] as JsonObject ?? throw new BusinessRuleException($"Entry {i + 1} is not an object.");
            var resourceType = Text(entry, "resourceType");
            if (resourceType is null || !char.IsUpper(resourceType[0]) || !resourceType.All(char.IsAsciiLetter))
            {
                throw new BusinessRuleException($"Entry {i + 1} needs a FHIR resource type, such as AllergyIntolerance.");
            }

            var label = $"{resourceType} (entry {i + 1})";
            var template = entry["template"] as JsonObject
                ?? throw new BusinessRuleException($"{label}: add the template that builds a {resourceType} from each row.");
            if (Text(template, "resourceType") is { } templateType && templateType != resourceType)
            {
                throw new BusinessRuleException($"{label}: the template builds a {templateType}, not a {resourceType}.");
            }

            template = (JsonObject)template.DeepClone();
            template["resourceType"] = resourceType;
            var parsed = TabularFhirTemplateEngine.ParseTemplates(
                new JsonArray(new JsonObject { ["template"] = template }).ToJsonString()).Single();

            var query = Text(entry, "query");
            Guid? fileId = Guid.TryParse(Text(entry, "fileId"), out var id) && id != Guid.Empty ? id : null;
            if (csv && fileId is null)
            {
                throw new BusinessRuleException($"{label}: choose the CSV file its rows come from.");
            }

            if (!csv && query is null)
            {
                throw new BusinessRuleException($"{label}: enter the SELECT query that returns its rows.");
            }

            var filterColumn = csv ? Text(entry, "rowFilterColumn") : null;
            var filterValue = csv ? Text(entry, "rowFilterValue") : null;
            if ((filterColumn is null) != (filterValue is null))
            {
                throw new BusinessRuleException($"{label}: a row filter needs both a column and a value.");
            }

            streams.Add(new TabularStream(resourceType, parsed, csv ? null : query, csv ? fileId : null, filterColumn, filterValue));
        }

        return streams;
    }

    /// <summary>The rows the entry reads: all of them, or those its row filter keeps. A filter column the file does not
    /// have is refused rather than matching nothing, which would look like an empty file.</summary>
    public static TabularRows ApplyRowFilter(TabularRows rows, TabularStream stream)
    {
        if (stream.RowFilterColumn is not { } column)
        {
            return rows;
        }

        if (!rows.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
        {
            throw new BusinessRuleException(
                $"{stream.ResourceType}: the row filter column '{column}' is not in the file.");
        }

        var wanted = stream.RowFilterValue!.Trim();
        return rows with
        {
            Rows = rows.Rows
                .Where(row => row.TryGetValue(column, out var value)
                              && string.Equals(value?.Trim(), wanted, StringComparison.OrdinalIgnoreCase))
                .ToList(),
        };
    }

    private static string? Text(JsonObject obj, string property) =>
        obj[property] is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;
}
