using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Normalization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Normalization.Steps;

/// <summary>
/// Phase D — flattens a resource's <c>identifier</c> array into top-level scalar properties so destination
/// mappings can address a specific identifier (e.g. a payer member id) with a simple JSONPath (e.g.
/// <c>$.memberId</c>) instead of searching the array for a matching <c>system</c>. Rule-driven: the default
/// covers Epic's payer member id; additional rules can be supplied via configuration section
/// <c>Normalization:IdentifierFlattening</c>.
/// </summary>
public sealed class IdentifierFlatteningNormalizationStep : IResourceNormalizationStep
{
    public int Order => 11;

    private readonly IReadOnlyList<FlatteningRule> _rules;
    private readonly ILogger<IdentifierFlatteningNormalizationStep> _logger;

    public IdentifierFlatteningNormalizationStep(
        IConfiguration configuration,
        ILogger<IdentifierFlatteningNormalizationStep> logger)
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
            _logger.LogWarning(exception, "Identifier flattening skipped: resource {ResourceType} is not valid JSON.", request.ResourceType);
            return Task.FromResult(current);
        }

        if (root is not JsonObject resource || resource["identifier"] is not JsonArray identifiers)
        {
            return Task.FromResult(current);
        }

        var warnings = new List<string>(current.Warnings);
        var flattened = 0;

        foreach (var rule in _rules)
        {
            var value = ExtractValue(identifiers, rule);
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

        warnings.Add($"Flattened {flattened} identifier(s) into top-level properties.");

        return Task.FromResult(current with
        {
            NormalizedJson = resource.ToJsonString(),
            Warnings = warnings
        });
    }

    private static JsonNode? ExtractValue(JsonArray identifiers, FlatteningRule rule)
    {
        foreach (var identifier in identifiers.OfType<JsonObject>())
        {
            if (!string.Equals(identifier["system"]?.GetValue<string>(), rule.System, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = identifier["value"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return JsonValue.Create(value);
            }
        }

        return null;
    }

    private static IReadOnlyList<FlatteningRule> LoadRules(IConfiguration configuration)
    {
        var configured = configuration
            .GetSection("Normalization:IdentifierFlattening")
            .Get<List<FlatteningRule>>();

        if (configured is { Count: > 0 })
        {
            return configured.Where(rule =>
                    !string.IsNullOrWhiteSpace(rule.System) &&
                    !string.IsNullOrWhiteSpace(rule.TargetProperty))
                .ToList();
        }

        return DefaultRules;
    }

    private static readonly IReadOnlyList<FlatteningRule> DefaultRules =
    [
        new FlatteningRule
        {
            System = "https://open.epic.com/FHIR/StructureDefinition/PayerMemberId",
            TargetProperty = "memberId"
        }
    ];

    public sealed class FlatteningRule
    {
        public string System { get; set; } = string.Empty;
        public string TargetProperty { get; set; } = string.Empty;
    }
}
