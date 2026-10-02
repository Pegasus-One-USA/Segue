using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Services.Tabular;

/// <summary>One row → FHIR template: which resource it builds, and the resource as FHIR JSON with placeholders.</summary>
public sealed record TabularFhirTemplate(string ResourceType, JsonObject Template);

/// <summary>A resource built from one row.</summary>
public sealed record TabularRenderedResource(string ResourceType, string? ResourceId, string Json, int RowNumber);

/// <summary>
/// Builds FHIR resources from table rows. A template is a FHIR resource in JSON whose string values may hold
/// placeholders:
/// <list type="bullet">
/// <item><c>"{{column}}"</c> as the whole value: the cell, as a string;</item>
/// <item><c>"{{column|format}}"</c>: the cell converted — <c>number</c>, <c>integer</c>, <c>boolean</c>,
/// <c>date</c> (to YYYY-MM-DD), <c>datetime</c> (to ISO 8601, UTC when no offset), <c>base64</c> (UTF-8, for note
/// text), <c>lower</c>, <c>upper</c>;</item>
/// <item><c>"Patient/{{patient_id}}"</c>: text with placeholders inside.</item>
/// </list>
/// An element whose placeholder's cell is empty is removed, and objects and arrays left empty are removed with
/// it, so one template serves rows that fill different columns. Column names ignore case.
///
/// <para><b>PHI.</b> Errors name the row number, template and column, never a cell value.</para>
/// </summary>
public static partial class TabularFhirTemplateEngine
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd", "yyyyMMdd", "MM/dd/yyyy", "M/d/yyyy", "MM-dd-yyyy", "M-d-yyyy", "yyyy/MM/dd", "dd.MM.yyyy",
        "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ", "yyyy-MM-ddTHH:mm:ss.fffZ", "yyyy-MM-dd HH:mm:ss",
        "MM/dd/yyyy HH:mm", "MM/dd/yyyy HH:mm:ss", "M/d/yyyy H:mm", "M/d/yyyy h:mm tt",
    ];

    private static readonly HashSet<string> Formats = new(StringComparer.Ordinal)
    {
        "string", "number", "integer", "boolean", "date", "datetime", "base64", "lower", "upper",
    };

    /// <summary>Parses the stored templates (a JSON array of <c>{resourceType, template}</c>) and checks each one
    /// names a resource type and uses only known formats.</summary>
    public static IReadOnlyList<TabularFhirTemplate> ParseTemplates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new BusinessRuleException("Add at least one template that turns a row into a FHIR resource.");
        }

        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            throw new BusinessRuleException("The templates are not valid JSON.");
        }

        if (array is null || array.Count == 0)
        {
            throw new BusinessRuleException("Add at least one template that turns a row into a FHIR resource.");
        }

        if (array.Count > TabularSourceSettings.MaxTemplates)
        {
            throw new BusinessRuleException($"At most {TabularSourceSettings.MaxTemplates} templates are supported.");
        }

        var templates = new List<TabularFhirTemplate>();
        foreach (var item in array)
        {
            var template = (item as JsonObject)?["template"] as JsonObject;
            var resourceType = template?["resourceType"]?.GetValueKind() == JsonValueKind.String
                ? template["resourceType"]!.GetValue<string>()
                : null;
            if (template is null || string.IsNullOrWhiteSpace(resourceType) || !char.IsUpper(resourceType[0]))
            {
                throw new BusinessRuleException("Every template must be a FHIR resource with a resourceType.");
            }

            foreach (var format in Placeholders(template).Select(p => p.Format).Where(f => f is not null))
            {
                if (!Formats.Contains(format!))
                {
                    throw new BusinessRuleException($"The {resourceType} template uses an unknown format '{format}'.");
                }
            }

            templates.Add(new TabularFhirTemplate(resourceType, (JsonObject)template.DeepClone()));
        }

        return templates;
    }

    /// <summary>The columns the templates read, for checking them against the table's header.</summary>
    public static IReadOnlySet<string> ReferencedColumns(IEnumerable<TabularFhirTemplate> templates) =>
        templates.SelectMany(t => Placeholders(t.Template)).Select(p => p.Column)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>Renders every template against one row. <paramref name="rowNumber"/> is 1-based, for errors.</summary>
    public static (IReadOnlyList<TabularRenderedResource> Resources, IReadOnlyList<string> Errors) Render(
        IReadOnlyList<TabularFhirTemplate> templates,
        IReadOnlyDictionary<string, string?> row,
        int rowNumber)
    {
        var resources = new List<TabularRenderedResource>();
        var errors = new List<string>();
        foreach (var template in templates)
        {
            var context = new RenderContext(row, rowNumber, template.ResourceType);
            var rendered = RenderNode(template.Template, context) as JsonObject;
            if (context.Errors.Count > 0)
            {
                errors.AddRange(context.Errors);
                continue;
            }

            // A row that fills none of the template's columns produces nothing for it, rather than a bare shell.
            if (rendered is null || rendered.Count <= 1 || !context.AnyValue)
            {
                continue;
            }

            rendered["resourceType"] = template.ResourceType;
            var id = rendered["id"]?.GetValueKind() == JsonValueKind.String ? rendered["id"]!.GetValue<string>() : null;
            if (id is not null && !FhirId().IsMatch(id))
            {
                errors.Add($"Row {rowNumber}: the {template.ResourceType} id is not a valid FHIR id (letters, digits, '-' and '.', up to 64).");
                continue;
            }

            resources.Add(new TabularRenderedResource(template.ResourceType, id, rendered.ToJsonString(), rowNumber));
        }

        return (resources, errors);
    }

    private static JsonNode? RenderNode(JsonNode? node, RenderContext context)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var result = new JsonObject();
                foreach (var (key, value) in obj)
                {
                    var rendered = RenderNode(value, context);
                    if (rendered is not null)
                    {
                        result[key] = rendered;
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonArray array:
            {
                var result = new JsonArray();
                foreach (var item in array)
                {
                    var rendered = RenderNode(item, context);
                    if (rendered is not null)
                    {
                        result.Add(rendered);
                    }
                }

                return result.Count == 0 ? null : result;
            }

            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                return RenderString(value.GetValue<string>(), context);

            default:
                return node?.DeepClone();
        }
    }

    private static JsonNode? RenderString(string text, RenderContext context)
    {
        var matches = Placeholder().Matches(text);
        if (matches.Count == 0)
        {
            return JsonValue.Create(text);
        }

        // The whole value is one placeholder: it may change type.
        if (matches.Count == 1 && matches[0].Value.Length == text.Length)
        {
            var column = matches[0].Groups["column"].Value.Trim();
            var format = matches[0].Groups["format"].Success ? matches[0].Groups["format"].Value.Trim() : null;
            var cell = context.Cell(column);
            if (cell is null)
            {
                return null;
            }

            context.AnyValue = true;
            return Convert(cell, format, column, context);
        }

        var builder = new StringBuilder();
        var last = 0;
        foreach (Match match in matches)
        {
            var cell = context.Cell(match.Groups["column"].Value.Trim());
            if (cell is null)
            {
                return null;
            }

            builder.Append(text, last, match.Index - last).Append(cell);
            last = match.Index + match.Length;
        }

        context.AnyValue = true;
        builder.Append(text, last, text.Length - last);
        return JsonValue.Create(builder.ToString());
    }

    private static JsonNode? Convert(string cell, string? format, string column, RenderContext context)
    {
        switch (format)
        {
            case null or "string":
                return JsonValue.Create(cell);
            case "lower":
                return JsonValue.Create(cell.ToLowerInvariant());
            case "upper":
                return JsonValue.Create(cell.ToUpperInvariant());
            case "base64":
                return JsonValue.Create(System.Convert.ToBase64String(Encoding.UTF8.GetBytes(cell)));
            case "number" when decimal.TryParse(cell, NumberStyles.Number, CultureInfo.InvariantCulture, out var number):
                return JsonValue.Create(number);
            case "integer" when long.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer):
                return JsonValue.Create(integer);
            case "boolean" when ParseBoolean(cell) is { } flag:
                return JsonValue.Create(flag);
            case "date" when ParseDate(cell) is { } date:
                return JsonValue.Create(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case "datetime" when ParseDateTime(cell) is { } instant:
                return JsonValue.Create(instant.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture));
            default:
                context.Errors.Add($"Row {context.RowNumber}: column '{column}' is not a valid {format} for the {context.ResourceType} template.");
                return null;
        }
    }

    private static bool? ParseBoolean(string cell) => cell.Trim().ToLowerInvariant() switch
    {
        "true" or "yes" or "y" or "1" => true,
        "false" or "no" or "n" or "0" => false,
        _ => null,
    };

    private static DateOnly? ParseDate(string cell) =>
        DateTime.TryParseExact(cell.Trim(), DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed)
            ? DateOnly.FromDateTime(parsed)
            : null;

    private static DateTimeOffset? ParseDateTime(string cell)
    {
        var trimmed = cell.Trim();
        if (DateTimeOffset.TryParse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var withOffset)
            && (trimmed.Contains('T') || trimmed.Contains(':')))
        {
            return withOffset;
        }

        return DateTime.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Utc))
            : null;
    }

    private static IEnumerable<(string Column, string? Format)> Placeholders(JsonNode? node) => node switch
    {
        JsonObject obj => obj.SelectMany(p => Placeholders(p.Value)),
        JsonArray array => array.SelectMany(Placeholders),
        JsonValue value when value.GetValueKind() == JsonValueKind.String =>
            Placeholder().Matches(value.GetValue<string>()).Select(m => (
                m.Groups["column"].Value.Trim(),
                m.Groups["format"].Success ? m.Groups["format"].Value.Trim() : (string?)null)),
        _ => [],
    };

    private sealed class RenderContext
    {
        private readonly IReadOnlyDictionary<string, string?> _row;

        public RenderContext(IReadOnlyDictionary<string, string?> row, int rowNumber, string resourceType)
        {
            _row = row;
            RowNumber = rowNumber;
            ResourceType = resourceType;
        }

        public int RowNumber { get; }

        public string ResourceType { get; }

        public List<string> Errors { get; } = [];

        /// <summary>At least one placeholder had a value; a template filled only by constants builds nothing.</summary>
        public bool AnyValue { get; set; }

        public string? Cell(string column)
        {
            if (_row.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }

            // Rows from a SQL reader are keyed as the database spells the column; look past case differences.
            var match = _row.FirstOrDefault(p => string.Equals(p.Key, column, StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(match.Value) ? null : match.Value.Trim();
        }
    }

    [GeneratedRegex(@"\{\{\s*(?<column>[^{}|]+?)\s*(\|\s*(?<format>[a-z0-9]+)\s*)?\}\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z0-9\-\.]{1,64}$")]
    private static partial Regex FhirId();
}
