using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>15. String Normalization &amp; Cleaning.</summary>
public sealed class StringNormalizationNode : ITransformNode
{
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ControlCharPattern = new(@"\p{Cc}", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.StringNormalization;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (raw is null)
        {
            return TransformResult.Ok(null);
        }

        var result = WhitespacePattern.Replace(raw.Trim(), " ");
        result = ControlCharPattern.Replace(result, string.Empty);

        if (config.GetBool("stripDiacritics", true))
        {
            result = StripDiacritics(result);
        }

        result = config.Get("case", "none") switch
        {
            "upper" => result.ToUpperInvariant(),
            "lower" => result.ToLowerInvariant(),
            "title" => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.ToLowerInvariant()),
            _ => result
        };

        var pattern = config.GetOrNull("regexPattern");
        if (pattern is not null)
        {
            try
            {
                result = Regex.Replace(result, pattern, config.Get("regexReplacement"));
            }
            catch (RegexParseException ex)
            {
                // An invalid pattern is a configuration mistake, not a data problem — fail this node
                // explicitly (routed through the rule's own ErrorPolicy) rather than let the exception
                // escape uncaught and take down whatever else was processing in the same batch.
                return TransformResult.Fail($"Invalid regex pattern '{pattern}': {ex.Message}");
            }
        }

        var maxLength = config.GetInt("maxLength", 0);
        if (maxLength > 0 && result.Length > maxLength)
        {
            result = result[..maxLength];
        }

        return TransformResult.Ok(string.IsNullOrEmpty(result) ? null : result);
    }

    private static string StripDiacritics(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>16. Concatenation / Templating / Splitting. Expects <paramref name="value"/> to be an
/// <see cref="IEnumerable{T}"/> of values to join (concat mode) or a single string (split mode).</summary>
public sealed class ConcatenationTemplatingNode : ITransformNode
{
    private static readonly Regex UnboundPlaceholder = new(@"\{\d+\}", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.ConcatenationTemplating;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        if (config.Get("mode", "concat") == "split")
        {
            var raw = value?.ToString();
            if (string.IsNullOrEmpty(raw))
            {
                return TransformResult.Ok(Array.Empty<string>());
            }

            var delimiter = config.Get("splitDelimiter", ",");
            var split = config.GetBool("splitIsRegex", false)
                ? Regex.Split(raw, delimiter).Select(s => s.Trim())
                : raw.Split(delimiter, StringSplitOptions.TrimEntries);
            var splitParts = split.Where(s => s.Length > 0).ToArray();

            // A template in SPLIT mode renders the parts the split just produced: "Hi {0}" against
            // "Physician Family Medicine, MD" gives "Hi Physician Family Medicine". Without one the parts are
            // handed on as an array, for a downstream node (ArrayListOperations, or a second pass in concat
            // mode) to consume — which is what split exists for.
            var splitTemplate = config.GetOrNull("template");
            return TransformResult.Ok(splitTemplate is null ? splitParts : FillTemplate(splitTemplate, splitParts));
        }

        var parts = value.AsItems().Select(v => v?.ToString()).ToArray();

        // Template mode: "{0} {1}" positional placeholders bound to the ordered input items, instead of a
        // plain separator-join — lets a display string mix literal text with the source fields
        // ("Dr. {0} {1}, MD"), which a bare separator can't express.
        var template = config.GetOrNull("template");
        if (template is not null)
        {
            return TransformResult.Ok(FillTemplate(template, parts));
        }

        var nonNullParts = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        var separator = config.Get("separator", " ");

        return TransformResult.Ok(nonNullParts.Length == 0 ? null : string.Join(separator, nonNullParts));
    }

    /// <summary>Binds "{0}", "{1}", ... positionally to <paramref name="parts"/>. Shared by both modes: concat
    /// binds the column's source fields, split binds the pieces the split produced.</summary>
    /// <remarks>
    /// A placeholder with no value behind it renders as nothing, rather than surviving as the literal text
    /// "{1}" in the destination column. A template outliving its inputs is a configuration mistake — "Mr {0}
    /// {1} Sir" left on a column that later dropped to a single source, say — and writing template syntax into
    /// what is usually a patient-facing field is the worst of the available outcomes: it looks like data, so
    /// nothing downstream flags it.
    /// </remarks>
    private static string? FillTemplate(string template, IReadOnlyList<string?> parts)
    {
        var filled = template;
        for (var i = 0; i < parts.Count; i++)
        {
            filled = filled.Replace($"{{{i}}}", parts[i] ?? string.Empty);
        }

        filled = UnboundPlaceholder.Replace(filled, string.Empty);
        return string.IsNullOrWhiteSpace(filled) ? null : filled;
    }
}

/// <summary>17. Array / List Operations. Expects <paramref name="value"/> to be an
/// <see cref="IEnumerable{T}"/>.</summary>
public sealed class ArrayListOperationsNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.ArrayListOperations;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        // A field mapped with ArrayPolicy.StoreJson arrives here as a single JSON-encoded string (e.g.
        // `["a","b","c"]`), not a real collection — AsItems() correctly treats a plain string as one opaque
        // item (so a genuine scalar string isn't accidentally split into characters), which otherwise makes
        // every operation here see "one item" (the whole JSON blob) instead of the real array. Unwrap a
        // JSON-array-shaped string into real items first; anything that isn't valid JSON array syntax falls
        // through to the normal single-item behavior unchanged.
        var items = TryUnwrapJsonArrayString(value, out var unwrapped) ? unwrapped : value.AsItems().ToList();

        return config.Get("operation", "first") switch
        {
            "last" => TransformResult.Ok(items.Count > 0 ? items[^1] : null),
            "nth" => TransformResult.Ok(TryNth(items, config.GetInt("index", 0))),
            "dedupe" => TransformResult.Ok(items.Select(i => i?.ToString()).Distinct().ToArray()),
            "join" => TransformResult.Ok(string.Join(config.Get("separator", ","), items.Select(i => i?.ToString()))),
            "count" => TransformResult.Ok(items.Count),
            "filter" => TransformResult.Ok(Filter(items, config.GetOrNull("predicateField"), config.GetOrNull("predicateValue")).ToArray()),
            "flatten" => TransformResult.Ok(Flatten(items).ToArray()),
            _ => TransformResult.Ok(items.Count > 0 ? items[0] : null)
        };
    }

    private static bool TryUnwrapJsonArrayString(object? value, out List<object?> items)
    {
        items = [];
        if (value is not string s || !s.TrimStart().StartsWith('['))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            items = doc.RootElement.EnumerateArray()
                .Select(element => (object?)(element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString()))
                .ToList();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object? TryNth(IReadOnlyList<object?> items, int index) =>
        index >= 0 && index < items.Count ? items[index] : null;

    /// <summary>Simple equality predicate (e.g. <c>identifier.where(system='...MRN')</c> from the spec) —
    /// a full FHIRPath predicate engine is out of scope per the PDF's own "examples, not mandates" note.
    /// Supports a scalar item (compared directly) or a JSON object item (compared on <paramref
    /// name="predicateField"/>).</summary>
    private static IEnumerable<object?> Filter(IEnumerable<object?> items, string? predicateField, string? predicateValue)
    {
        if (predicateValue is null)
        {
            return items;
        }

        return items.Where(item =>
        {
            if (predicateField is not null && item is System.Text.Json.Nodes.JsonObject obj)
            {
                return string.Equals(obj[predicateField]?.ToString(), predicateValue, StringComparison.OrdinalIgnoreCase);
            }

            return string.Equals(item?.ToString(), predicateValue, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>Flattens one level of nested arrays — an item that is itself an <see cref="IEnumerable"/>
    /// (excluding strings) contributes its own items instead of itself.</summary>
    private static IEnumerable<object?> Flatten(IEnumerable<object?> items) =>
        items.SelectMany(item => item is not string && item is System.Collections.IEnumerable nested
            ? nested.Cast<object?>()
            : [item]);
}
