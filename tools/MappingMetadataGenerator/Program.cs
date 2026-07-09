using System.Globalization;
using System.Text.Json;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;

// Generates src/FHIRBridge.Application/Mapping/Catalog/fhir-r4-catalog.json from the Firely R4 model
// via introspection (no FHIR spec zip needed — the POCO ClassMappings carry element names, collection
// flags, choice types and cardinality). Produces array-aware JSONPaths ($.name[*].given[*]) plus the
// arrayAncestors the mapping engine needs, so the wizard no longer has to guess at array-ness.

const int MaxDepth = 3;
string[] targetResources =
[
    "Patient", "Observation", "Encounter", "Condition", "MedicationRequest", "AllergyIntolerance",
];

// Element names that add noise to a mapping picker — skipped at every level.
var skip = new HashSet<string>(StringComparer.Ordinal)
{
    "extension", "modifierExtension", "contained", "meta", "text",
    "implicitRules", "language", "id",
};

var inspector = ModelInfo.ModelInspector;
var resources = new Dictionary<string, List<CatalogField>>(StringComparer.Ordinal);

foreach (var resourceName in targetResources)
{
    var mapping = inspector.FindClassMapping(resourceName);
    if (mapping is null)
    {
        Console.Error.WriteLine($"WARN: no class mapping for {resourceName}");
        continue;
    }

    var fields = new List<CatalogField>();
    var seen = new HashSet<string>(StringComparer.Ordinal);
    Walk(mapping, "", "$", [], [resourceName], 0, fields, seen);
    // id first, then alphabetical by fhirPath — stable, friendly for the picker.
    fields.Sort((a, b) => string.CompareOrdinal(a.FhirPath, b.FhirPath));
    resources[resourceName] = fields;
    Console.WriteLine($"{resourceName}: {fields.Count} fields");
}

var catalog = new
{
    fhirVersion = "4.0.1",
    generatedBy = "tools/MappingMetadataGenerator (Firely R4 introspection)",
    resources = resources.ToDictionary(
        kv => kv.Key,
        kv => kv.Value.Select(f => new
        {
            label = f.Label,
            jsonPath = f.JsonPath,
            fhirPath = f.FhirPath,
            cardinality = f.Cardinality,
            valueType = f.ValueType,
            isArray = f.IsArray,
            arrays = f.Arrays,
        })),
};

var outPath = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "FHIRBridge.Application", "Mapping", "Catalog", "fhir-r4-catalog.json"));

var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(outPath, json);
Console.WriteLine($"Wrote {json.Length} bytes -> {outPath}");

void Walk(
    ClassMapping mapping,
    string fhirPath,
    string jsonPath,
    IReadOnlyList<string> arrayAncestors,
    IReadOnlyCollection<string> visited,
    int depth,
    List<CatalogField> fields,
    HashSet<string> seen)
{
    foreach (var pm in mapping.PropertyMappings)
    {
        if (skip.Contains(pm.Name) && !(depth == 0 && pm.Name == "id"))
        {
            continue;
        }

        // Choice elements (value[x]) fan out into one concrete entry per allowed type (valueQuantity, …).
        var isChoice = pm.Choice == ChoiceType.DatatypeChoice;
        var candidateTypes = isChoice ? pm.FhirType : [pm.ImplementingType];

        foreach (var clrType in candidateTypes)
        {
            var typeMapping = inspector.FindOrImportClassMapping(clrType);
            if (typeMapping is null)
            {
                continue;
            }

            var elementJson = isChoice ? pm.Name + Capitalize(typeMapping.Name) : pm.Name;
            var childFhirPath = fhirPath.Length == 0 ? elementJson : $"{fhirPath}.{elementJson}";
            var childJsonSeg = pm.IsCollection ? $"{elementJson}[*]" : elementJson;
            var childJsonPath = $"{jsonPath}.{childJsonSeg}";
            var childArrayAncestors = pm.IsCollection ? [.. arrayAncestors, childFhirPath] : arrayAncestors;

            if (typeMapping.IsFhirPrimitive)
            {
                AddLeaf(childFhirPath, childJsonPath, pm.IsCollection, arrayAncestors, typeMapping.Name, fields, seen);
                continue;
            }

            // Complex type: recurse until depth/cycle limit, then stop (a too-deep complex element
            // isn't emitted as a scalar column — the user maps its leaf children instead).
            if (depth + 1 <= MaxDepth && !visited.Contains(typeMapping.Name))
            {
                Walk(typeMapping, childFhirPath, childJsonPath, childArrayAncestors,
                    [.. visited, typeMapping.Name], depth + 1, fields, seen);
            }
        }
    }
}

void AddLeaf(
    string fhirPath,
    string jsonPath,
    bool isArray,
    IReadOnlyList<string> arrayAncestors,
    string fhirTypeName,
    List<CatalogField> fields,
    HashSet<string> seen)
{
    if (!seen.Add(jsonPath))
    {
        return;
    }

    fields.Add(new CatalogField(
        Label: Prettify(fhirPath),
        JsonPath: jsonPath,
        FhirPath: fhirPath,
        Cardinality: isArray ? "0..*" : "0..1",
        ValueType: ValueTypeFor(fhirTypeName),
        IsArray: isArray,
        Arrays: arrayAncestors));
}

static string ValueTypeFor(string fhirTypeName) => fhirTypeName switch
{
    "boolean" => "Boolean",
    "integer" or "positiveInt" or "unsignedInt" => "Integer",
    "decimal" => "Decimal",
    "date" => "Date",
    "dateTime" or "instant" => "DateTime",
    _ => "String",
};

static string Capitalize(string s) =>
    string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

// "name.given" -> "Name Given"; "valueQuantity.value" -> "Value Quantity Value" (readable picker label).
static string Prettify(string fhirPath)
{
    var parts = new List<string>();
    foreach (var seg in fhirPath.Split('.'))
    {
        var words = System.Text.RegularExpressions.Regex.Replace(seg, "([a-z0-9])([A-Z])", "$1 $2");
        parts.Add(CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words));
    }
    return string.Join(" › ", parts);
}

internal sealed record CatalogField(
    string Label,
    string JsonPath,
    string FhirPath,
    string Cardinality,
    string ValueType,
    bool IsArray,
    IReadOnlyList<string> Arrays);
