using System.Collections.Concurrent;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// Loads a generated FHIR element catalog (copied next to the assembly under <c>Mapping/Catalog/</c>)
/// once and caches it. Singleton per <paramref name="catalogFileName"/> — see the keyed DI registration
/// in DependencyInjection.cs, which instantiates this class once per vendor catalog ("fhir-r4-catalog.json",
/// the generic base-FHIR-R4 catalog from tools/MappingMetadataGenerator; "fhir-r4-catalog.epic.json", the
/// Epic-profile catalog from tools/EpicTemplateCatalogGenerator).
/// </summary>
public sealed class EmbeddedFhirElementCatalog : IFhirElementCatalog
{
    private readonly string _catalogPath;
    private readonly Lazy<CatalogData> _data;
    private readonly ConcurrentDictionary<string, IReadOnlyList<FhirElementDto>> _fieldCache = new(StringComparer.OrdinalIgnoreCase);

    public EmbeddedFhirElementCatalog(string catalogFileName = "fhir-r4-catalog.json")
    {
        _catalogPath = Path.Combine(AppContext.BaseDirectory, "Mapping", "Catalog", catalogFileName);
        _data = new Lazy<CatalogData>(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public string FhirVersion => _data.Value.FhirVersion;

    public IReadOnlyList<string> ResourceTypes => _data.Value.ResourceTypes;

    public IReadOnlyList<FhirElementDto> Fields(string resourceType)
    {
        if (string.IsNullOrWhiteSpace(resourceType))
        {
            return [];
        }

        return _fieldCache.GetOrAdd(resourceType, key =>
            _data.Value.Resources.TryGetValue(key, out var fields) ? fields : []);
    }

    private CatalogData Load()
    {
        if (!File.Exists(_catalogPath))
        {
            return new CatalogData("4.0.1", [], new Dictionary<string, IReadOnlyList<FhirElementDto>>(StringComparer.OrdinalIgnoreCase));
        }

        using var stream = File.OpenRead(_catalogPath);
        using var document = JsonDocument.Parse(stream);
        var rootElement = document.RootElement;

        var fhirVersion = rootElement.TryGetProperty("fhirVersion", out var v) ? v.GetString() ?? "4.0.1" : "4.0.1";

        var resources = new Dictionary<string, IReadOnlyList<FhirElementDto>>(StringComparer.OrdinalIgnoreCase);
        if (rootElement.TryGetProperty("resources", out var resourcesEl) && resourcesEl.ValueKind == JsonValueKind.Object)
        {
            foreach (var resource in resourcesEl.EnumerateObject())
            {
                var list = new List<FhirElementDto>();
                foreach (var f in resource.Value.EnumerateArray())
                {
                    list.Add(new FhirElementDto(
                        f.GetProperty("label").GetString() ?? string.Empty,
                        f.GetProperty("jsonPath").GetString() ?? string.Empty,
                        f.GetProperty("fhirPath").GetString() ?? string.Empty,
                        f.GetProperty("cardinality").GetString() ?? string.Empty,
                        f.TryGetProperty("valueType", out var vt) ? vt.GetString() ?? "String" : "String",
                        f.TryGetProperty("isArray", out var ia) && ia.GetBoolean(),
                        f.TryGetProperty("arrays", out var arr) && arr.ValueKind == JsonValueKind.Array
                            ? arr.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToList()
                            : []));
                }

                resources[resource.Name] = list;
            }
        }

        var resourceTypes = resources.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new CatalogData(fhirVersion, resourceTypes, resources);
    }

    private sealed record CatalogData(
        string FhirVersion,
        IReadOnlyList<string> ResourceTypes,
        IReadOnlyDictionary<string, IReadOnlyList<FhirElementDto>> Resources);
}
