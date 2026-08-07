using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Transforms.Nodes;

/// <summary>7. Value / Code Mapping (Lookup Translation) — a static field-grain ConceptMap.
/// <c>config["map"]</c> is a JSON object string, e.g. <c>{"M":"male","F":"female"}</c>.</summary>
public sealed class ValueCodeMappingNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.ValueCodeMapping;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(null);
        }

        var map = ParseMap(config.Get("map", "{}"));
        var caseInsensitive = config.GetBool("caseInsensitive", true);
        var comparer = caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        var match = map.FirstOrDefault(kv => comparer.Equals(kv.Key, raw));
        if (match.Key is not null)
        {
            return TransformResult.Ok(match.Value);
        }

        return config.Get("unmatchedPolicy", "null") switch
        {
            "passThrough" => TransformResult.Ok(raw),
            "error" => TransformResult.Fail($"No mapping configured for '{raw}'."),
            _ => TransformResult.Ok(null)
        };
    }

    internal static Dictionary<string, string> ParseMap(string mapJson)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(mapJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

/// <summary>8. CodeableConcept / Coding Builder — assembles a coded element from a bare code + config.</summary>
public sealed class CodeableConceptBuilderNode : ITransformNode
{
    private static readonly IReadOnlyDictionary<string, string> SystemUris = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["LOINC"] = "http://loinc.org",
        ["SNOMED"] = "http://snomed.info/sct",
        ["ICD10"] = "http://hl7.org/fhir/sid/icd-10-cm",
        ["RXNORM"] = "http://www.nlm.nih.gov/research/umls/rxnorm",
        ["NPI"] = "http://hl7.org/fhir/sid/us-npi"
    };

    public TransformNodeType NodeType => TransformNodeType.CodeableConceptBuilder;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var code = value?.ToString();
        if (string.IsNullOrWhiteSpace(code))
        {
            return TransformResult.Ok(null);
        }

        var systemKey = config.Get("system");
        var systemUri = SystemUris.GetValueOrDefault(systemKey, systemKey);
        var display = config.GetOrNull("display");

        var coding = new JsonObject { ["system"] = systemUri, ["code"] = code };
        if (display is not null)
        {
            coding["display"] = display;
        }

        var concept = new JsonObject { ["coding"] = new JsonArray(coding) };
        if (config.GetBool("includeText", true))
        {
            concept["text"] = display ?? code;
        }

        return TransformResult.Ok(concept);
    }
}

/// <summary>9. Status / Enum Coercion (Required Binding) — like <see cref="ValueCodeMappingNode"/> but never
/// emits an out-of-value-set code: unmatched input always falls back to <c>config["fallback"]</c> (default
/// "unknown") rather than passing through or erroring.</summary>
public sealed class StatusEnumCoercionNode : ITransformNode
{
    public TransformNodeType NodeType => TransformNodeType.StatusEnumCoercion;

    public TransformResult Execute(object? value, IReadOnlyDictionary<string, string> config, string? secret)
    {
        var raw = value?.ToString()?.Trim();
        var fallback = config.Get("fallback", "unknown");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return TransformResult.Ok(fallback);
        }

        var map = ValueCodeMappingNode.ParseMap(config.Get("map", "{}"));
        var match = map.FirstOrDefault(kv => string.Equals(kv.Key, raw, StringComparison.OrdinalIgnoreCase));
        return TransformResult.Ok(match.Key is not null ? match.Value : fallback);
    }
}
