using System.Globalization;
using System.Text;
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
            return TransformResult.Ok(split.Where(s => s.Length > 0).ToArray());
        }

        var parts = value.AsItems().Select(v => v?.ToString()).ToArray();

        // Template mode: "{0} {1}" positional placeholders bound to the ordered input items, instead of a
        // plain separator-join — lets a display string mix literal text with the source fields
        // ("Dr. {0} {1}, MD"), which a bare separator can't express.
        var template = config.GetOrNull("template");
        if (template is not null)
        {
            var filled = template;
            for (var i = 0; i < parts.Length; i++)
            {
                filled = filled.Replace($"{{{i}}}", parts[i] ?? string.Empty);
            }

            return TransformResult.Ok(string.IsNullOrWhiteSpace(filled) ? null : filled);
        }

        var nonNullParts = parts.Where(p => !string.IsNullOrEmpty(p)).ToArray();
        var separator = config.Get("separator", " ");

        return TransformResult.Ok(nonNullParts.Length == 0 ? null : string.Join(separator, nonNullParts));
    }
}

/// <summary>17. Array / List Operations. Expects <paramref name="value"/> to be an
/// <see cref="IEnumerable{T}"/>.</summary>
public sealed class ArrayListOperationsNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.ArrayListOperations;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var items = value.AsItems().ToList();

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
