using System.Globalization;
using System.Text.RegularExpressions;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>15. String Normalization &amp; Cleaning.</summary>
public sealed class StringNormalizationNode : ITransformNode
{
    private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);

    public TransformNodeType NodeType => TransformNodeType.StringNormalization;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (raw is null)
        {
            return TransformResult.Ok(null);
        }

        var result = WhitespacePattern.Replace(raw.Trim(), " ");

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
            return TransformResult.Ok(raw.Split(delimiter, StringSplitOptions.TrimEntries));
        }

        var parts = value.AsItems().Select(v => v?.ToString());
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
            _ => TransformResult.Ok(items.Count > 0 ? items[0] : null)
        };
    }

    private static object? TryNth(IReadOnlyList<object?> items, int index) =>
        index >= 0 && index < items.Count ? items[index] : null;
}
