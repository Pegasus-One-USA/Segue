using System.Text.Json;
using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.Services.Mapping.Internal;

/// <summary>
/// Flattens an arbitrary source JSON document into leaf fields, recursively — objects become dotted paths
/// (<c>name.given</c>), arrays fan out per item with both a concrete indexed path and an index-stripped
/// structural path. No allow-list: whatever paths exist in the document become candidates, up to whatever
/// depth/array size the document actually has.
/// </summary>
public static class JsonSchemaExtractor
{
    public static IReadOnlyList<ExtractedSourceField> Extract(JsonDocument document)
    {
        var results = new List<ExtractedSourceField>();
        Walk(document.RootElement, path: "$", structuralPath: "", results);
        return results;
    }

    private static void Walk(JsonElement element, string path, string structuralPath, List<ExtractedSourceField> results)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var childPath = $"{path}.{property.Name}";
                    var childStructural = structuralPath.Length == 0 ? property.Name : $"{structuralPath}.{property.Name}";
                    Walk(property.Value, childPath, childStructural, results);
                }
                break;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, $"{path}[{index}]", structuralPath, results);
                    index++;
                }
                break;

            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                results.Add(new ExtractedSourceField(
                    path,
                    structuralPath,
                    FieldNormalizer.Normalize(LeafName(structuralPath)),
                    MappingValueType.String,
                    SampleValue: null));
                break;

            default:
                results.Add(new ExtractedSourceField(
                    path,
                    structuralPath,
                    FieldNormalizer.Normalize(LeafName(structuralPath)),
                    InferDataType(element),
                    SampleValue: RawValue(element)));
                break;
        }
    }

    private static string LeafName(string structuralPath)
    {
        var lastDot = structuralPath.LastIndexOf('.');
        return lastDot < 0 ? structuralPath : structuralPath[(lastDot + 1)..];
    }

    private static MappingValueType InferDataType(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => MappingValueType.String,
        JsonValueKind.True or JsonValueKind.False => MappingValueType.Boolean,
        JsonValueKind.Number => element.TryGetInt64(out _) ? MappingValueType.Integer : MappingValueType.Decimal,
        _ => MappingValueType.Json
    };

    private static string RawValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? string.Empty,
        _ => element.GetRawText()
    };
}
