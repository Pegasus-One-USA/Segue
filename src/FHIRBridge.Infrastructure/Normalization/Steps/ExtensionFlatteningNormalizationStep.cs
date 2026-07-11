using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Normalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Normalization.Steps;

/// <summary>
/// Phase D — flattens FHIR/Epic extensions into top-level scalar properties so destination mappings can address them
/// with a simple JSONPath (e.g. <c>$.race</c>) instead of walking the <c>extension</c> array. Rule-driven: defaults
/// cover the common US Core extensions (race, ethnicity, birthsex); additional rules can be supplied via
/// configuration section <c>Normalization:ExtensionFlattening</c>.
/// </summary>
public sealed class ExtensionFlatteningNormalizationStep : IResourceNormalizationStep
{
    public int Order => 10;

    private readonly IReadOnlyList<FlatteningRule> _rules;
    private readonly ILogger<ExtensionFlatteningNormalizationStep> _logger;

    public ExtensionFlatteningNormalizationStep(
        IConfiguration configuration,
        ILogger<ExtensionFlatteningNormalizationStep> logger)
    {
        _logger = logger;
        _rules = LoadRules(configuration);
    }

    public Task<ResourceNormalizationResult> ApplyAsync(
        ResourceNormalizationRequest request,
        ResourceNormalizationResult current,
        CancellationToken cancellationToken)
    {
        if (_rules.Count == 0)
        {
            return Task.FromResult(current);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(current.NormalizedJson);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Extension flattening skipped: resource {ResourceType} is not valid JSON.", request.ResourceType);
            return Task.FromResult(current);
        }

        if (root is not JsonObject resource || resource["extension"] is not JsonArray extensions)
        {
            return Task.FromResult(current);
        }

        var warnings = new List<string>(current.Warnings);
        var flattened = 0;

        foreach (var rule in _rules)
        {
            var value = ExtractValue(extensions, rule);
            if (value is null)
            {
                continue;
            }

            resource[rule.TargetProperty] = value;
            flattened++;
        }

        if (flattened == 0)
        {
            return Task.FromResult(current);
        }

        warnings.Add($"Flattened {flattened} extension(s) into top-level properties.");

        return Task.FromResult(current with
        {
            NormalizedJson = resource.ToJsonString(),
            Warnings = warnings
        });
    }

    private static JsonNode? ExtractValue(JsonArray extensions, FlatteningRule rule)
    {
        foreach (var extension in extensions.OfType<JsonObject>())
        {
            if (!string.Equals(extension["url"]?.GetValue<string>(), rule.ExtensionUrl, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Nested extension (e.g. US Core race -> ombCategory / text).
            if (!string.IsNullOrWhiteSpace(rule.NestedUrl))
            {
                if (extension["extension"] is not JsonArray nested)
                {
                    continue;
                }

                foreach (var inner in nested.OfType<JsonObject>())
                {
                    if (string.Equals(inner["url"]?.GetValue<string>(), rule.NestedUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        var nestedValue = ReadScalar(inner);
                        if (nestedValue is not null)
                        {
                            return nestedValue;
                        }
                    }
                }

                continue;
            }

            var value = ReadScalar(extension);
            if (value is not null)
            {
                return value;
            }
        }

        return null;
    }

    // Reads the value[x] off an extension object as a flattenable scalar.
    private static JsonNode? ReadScalar(JsonObject extension)
    {
        foreach (var (name, node) in extension)
        {
            if (!name.StartsWith("value", StringComparison.Ordinal) || node is null)
            {
                continue;
            }

            if (node is JsonObject valueObject)
            {
                // valueCodeableConcept -> prefer the concept's own text, else the first coding's display/code.
                if (valueObject["coding"] is JsonArray codings)
                {
                    var text = valueObject["text"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return JsonValue.Create(text);
                    }

                    var firstCoding = codings.OfType<JsonObject>().FirstOrDefault();
                    var codingDisplay = firstCoding?["display"]?.GetValue<string>() ?? firstCoding?["code"]?.GetValue<string>();
                    return codingDisplay is null ? null : JsonValue.Create(codingDisplay);
                }

                // valueCoding -> display, fall back to code.
                var display = valueObject["display"]?.GetValue<string>() ?? valueObject["code"]?.GetValue<string>();
                return display is null ? null : JsonValue.Create(display);
            }

            if (node is JsonValue scalar)
            {
                return scalar.DeepClone();
            }
        }

        return null;
    }

    private static IReadOnlyList<FlatteningRule> LoadRules(IConfiguration configuration)
    {
        var configured = configuration
            .GetSection("Normalization:ExtensionFlattening")
            .Get<List<FlatteningRule>>();

        if (configured is { Count: > 0 })
        {
            return configured.Where(rule =>
                    !string.IsNullOrWhiteSpace(rule.ExtensionUrl) &&
                    !string.IsNullOrWhiteSpace(rule.TargetProperty))
                .ToList();
        }

        return DefaultRules;
    }

    private static readonly IReadOnlyList<FlatteningRule> DefaultRules =
    [
        new FlatteningRule
        {
            ExtensionUrl = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race",
            NestedUrl = "text",
            TargetProperty = "race"
        },
        new FlatteningRule
        {
            ExtensionUrl = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-race",
            NestedUrl = "ombCategory",
            TargetProperty = "raceCategory"
        },
        new FlatteningRule
        {
            ExtensionUrl = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-ethnicity",
            NestedUrl = "text",
            TargetProperty = "ethnicity"
        },
        new FlatteningRule
        {
            ExtensionUrl = "http://hl7.org/fhir/us/core/StructureDefinition/us-core-birthsex",
            TargetProperty = "birthsex"
        },
        new FlatteningRule
        {
            ExtensionUrl = "http://open.epic.com/FHIR/StructureDefinition/extension/legal-sex",
            TargetProperty = "legalSex"
        },
        new FlatteningRule
        {
            ExtensionUrl = "http://open.epic.com/FHIR/StructureDefinition/extension/sex-for-clinical-use",
            TargetProperty = "sexForClinicalUse"
        },
        new FlatteningRule
        {
            ExtensionUrl = "http://open.epic.com/FHIR/StructureDefinition/extension/calculated-pronouns-to-use-for-text",
            TargetProperty = "pronouns"
        }
    ];

    public sealed class FlatteningRule
    {
        public string ExtensionUrl { get; set; } = string.Empty;
        public string? NestedUrl { get; set; }
        public string TargetProperty { get; set; } = string.Empty;
    }
}
