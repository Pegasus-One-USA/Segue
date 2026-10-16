using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.SharedKernel.Exceptions;

namespace FHIRBridge.Application.Abstractions.Cds;

/// <summary>
/// An instance's settings with the handler's defaults filled in. Getters are lenient (a missing or wrongly typed
/// value returns the fallback) because stored settings were already checked by <see cref="Validate"/> on save.
/// </summary>
public sealed class CdsSettingsValues
{
    private readonly JsonObject _values;

    private CdsSettingsValues(JsonObject values)
    {
        _values = values;
    }

    /// <summary>A copy of the resolved settings, e.g. to echo on the Test screen.</summary>
    public JsonObject ToJsonObject() => (JsonObject)_values.DeepClone();

    public double GetNumber(string key, double fallback = 0) =>
        TryGetNumber(_values[key], out var number) ? number : fallback;

    public string? GetText(string key, string? fallback = null) =>
        _values[key] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : fallback;

    public bool GetBoolean(string key, bool fallback = false) =>
        _values[key] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False
            ? value.GetValue<bool>()
            : fallback;

    public IReadOnlyList<string> GetList(string key) =>
        _values[key] is JsonArray array
            ? array.OfType<JsonValue>()
                .Where(item => item.GetValueKind() == JsonValueKind.String)
                .Select(item => item.GetValue<string>())
                .ToArray()
            : [];

    /// <summary>Stored settings JSON plus each descriptor's default for any key left unset.</summary>
    public static CdsSettingsValues Resolve(IReadOnlyList<CdsSettingDescriptor> descriptors, string? settingsJson)
    {
        var values = ParseObjectOrEmpty(settingsJson);
        foreach (var descriptor in descriptors)
        {
            if (values[descriptor.Key] is null && descriptor.DefaultValue is not null)
            {
                values[descriptor.Key] = descriptor.DefaultValue.DeepClone();
            }
        }

        return new CdsSettingsValues(values);
    }

    /// <summary>
    /// Checks <paramref name="input"/> against the descriptors and returns the normalized object to store (unset
    /// and null keys dropped, text trimmed). Throws <see cref="BusinessRuleException"/> listing every problem.
    /// </summary>
    public static JsonObject Validate(IReadOnlyList<CdsSettingDescriptor> descriptors, JsonObject? input)
    {
        var errors = new List<string>();
        var normalized = new JsonObject();
        var byKey = descriptors.ToDictionary(d => d.Key, StringComparer.Ordinal);

        foreach (var (key, _) in input ?? [])
        {
            if (!byKey.ContainsKey(key))
            {
                errors.Add($"'{key}' is not a setting of this handler.");
            }
        }

        foreach (var descriptor in descriptors)
        {
            var node = input?[descriptor.Key];
            if (node is null)
            {
                if (descriptor.Required && descriptor.DefaultValue is null)
                {
                    errors.Add($"{descriptor.Label} is required.");
                }

                continue;
            }

            var error = ValidateValue(descriptor, node, out var value);
            if (error is not null)
            {
                errors.Add(error);
                continue;
            }

            normalized[descriptor.Key] = value;
        }

        if (errors.Count > 0)
        {
            throw new BusinessRuleException(string.Join(" ", errors));
        }

        return normalized;
    }

    private static string? ValidateValue(CdsSettingDescriptor descriptor, JsonNode node, out JsonNode? value)
    {
        value = null;
        var kind = node.GetValueKind();
        if (descriptor.Type == CdsSettingTypes.Number)
        {
            if (!TryGetNumber(node, out var number))
            {
                return $"{descriptor.Label} must be a number.";
            }

            if ((descriptor.Min is { } min && number < min) || (descriptor.Max is { } max && number > max))
            {
                return $"{descriptor.Label} must be between {Format(descriptor.Min)} and {Format(descriptor.Max)}.";
            }

            value = JsonValue.Create(number);
            return null;
        }

        if (descriptor.Type == CdsSettingTypes.Boolean)
        {
            if (kind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return $"{descriptor.Label} must be true or false.";
            }

            value = JsonValue.Create(node.GetValue<bool>());
            return null;
        }

        if (descriptor.Type == CdsSettingTypes.List)
        {
            if (node is not JsonArray array || array.Any(item => item is null || item.GetValueKind() != JsonValueKind.String))
            {
                return $"{descriptor.Label} must be a list of text values.";
            }

            value = new JsonArray(array
                .Select(item => item!.GetValue<string>().Trim())
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .Select(item => (JsonNode?)JsonValue.Create(item))
                .ToArray());
            return null;
        }

        // Text and Select are both strings; Select must also be one of its options.
        if (kind != JsonValueKind.String)
        {
            return $"{descriptor.Label} must be text.";
        }

        var text = node.GetValue<string>().Trim();
        if (descriptor.Type == CdsSettingTypes.Select
            && descriptor.Options is { Count: > 0 } options
            && !options.Any(option => option.Value == text))
        {
            return $"{descriptor.Label} must be one of: {string.Join(", ", options.Select(o => o.Value))}.";
        }

        value = JsonValue.Create(text);
        return null;
    }

    /// <summary>
    /// Reads a JSON number whatever backs the node. <c>JsonValue.TryGetValue&lt;double&gt;</c> alone fails for a value
    /// created from an int (e.g. a descriptor default of <c>JsonValue.Create(60)</c>), so the JSON text is parsed.
    /// </summary>
    private static bool TryGetNumber(JsonNode? node, out double number)
    {
        number = 0;
        return node is JsonValue value
               && value.GetValueKind() == JsonValueKind.Number
               && double.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);
    }

    private static string Format(double? bound) =>
        bound?.ToString(CultureInfo.InvariantCulture) ?? "any";

    private static JsonObject ParseObjectOrEmpty(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
