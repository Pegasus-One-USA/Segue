using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// HIPAA Safe Harbor-style de-identification. Applies a configurable set of FHIR-path redaction rules over the
/// resource JSON: direct identifiers are removed or hashed, dates are generalized to the year, and ZIP codes are
/// truncated to three digits. Rules can be supplied via <c>Deidentification:Rules</c>; sensible Safe Harbor defaults
/// are used otherwise. Replaces the pass-through stub that performed no masking.
/// </summary>
public sealed class SafeHarborDeIdentificationService : IDeIdentificationService
{
    private readonly IReadOnlyList<DeIdentificationRule> _rules;
    private readonly ILogger<SafeHarborDeIdentificationService> _logger;

    public SafeHarborDeIdentificationService(
        IConfiguration configuration,
        ILogger<SafeHarborDeIdentificationService>? logger = null)
    {
        _logger = logger ?? NullLogger<SafeHarborDeIdentificationService>.Instance;
        _rules = LoadRules(configuration);
    }

    public Task<string> DeIdentifyAsync(DeIdentificationRequest request, CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(request.RawJson);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "De-identification skipped: {ResourceType} is not valid JSON.", request.ResourceType);
            return Task.FromResult(request.RawJson);
        }

        if (root is not JsonObject resource)
        {
            return Task.FromResult(request.RawJson);
        }

        foreach (var rule in _rules)
        {
            if (rule.AppliesTo(request.ResourceType))
            {
                ApplyPath(resource, rule.PathSegments, 0, rule.Strategy);
            }
        }

        return Task.FromResult(resource.ToJsonString());
    }

    private static void ApplyPath(JsonNode? node, IReadOnlyList<string> path, int index, DeIdentificationStrategy strategy)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var element in array)
                {
                    ApplyPath(element, path, index, strategy);
                }

                break;

            case JsonObject obj when index == path.Count - 1:
                ApplyStrategy(obj, path[index], strategy);
                break;

            case JsonObject obj:
                if (obj.TryGetPropertyValue(path[index], out var child))
                {
                    ApplyPath(child, path, index + 1, strategy);
                }

                break;
        }
    }

    private static void ApplyStrategy(JsonObject parent, string property, DeIdentificationStrategy strategy)
    {
        if (!parent.TryGetPropertyValue(property, out var current) || current is null)
        {
            return;
        }

        switch (strategy)
        {
            case DeIdentificationStrategy.Remove:
                parent.Remove(property);
                break;

            case DeIdentificationStrategy.Redact:
                parent[property] = "[REDACTED]";
                break;

            case DeIdentificationStrategy.Hash:
                if (current is JsonValue hashValue && hashValue.TryGetValue<string>(out var raw))
                {
                    parent[property] = Hash(raw);
                }

                break;

            case DeIdentificationStrategy.GeneralizeDateToYear:
                if (current is JsonValue dateValue && dateValue.TryGetValue<string>(out var date) && date.Length >= 4)
                {
                    parent[property] = date[..4];
                }

                break;

            case DeIdentificationStrategy.GeneralizeZip3:
                if (current is JsonValue zipValue && zipValue.TryGetValue<string>(out var zip) && zip.Length >= 3)
                {
                    parent[property] = zip[..3] + "00";
                }

                break;
        }
    }

    private static string Hash(string value)
        => "anon-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16].ToLowerInvariant();

    private static IReadOnlyList<DeIdentificationRule> LoadRules(IConfiguration configuration)
    {
        var configured = configuration.GetSection("Deidentification:Rules").Get<List<RuleConfig>>();
        if (configured is { Count: > 0 })
        {
            return configured
                .Where(r => !string.IsNullOrWhiteSpace(r.Path) && Enum.TryParse<DeIdentificationStrategy>(r.Strategy, true, out _))
                .Select(r => new DeIdentificationRule(
                    string.IsNullOrWhiteSpace(r.ResourceType) ? "*" : r.ResourceType,
                    r.Path,
                    Enum.Parse<DeIdentificationStrategy>(r.Strategy, true)))
                .ToList();
        }

        return DefaultSafeHarborRules;
    }

    private static readonly IReadOnlyList<DeIdentificationRule> DefaultSafeHarborRules =
    [
        new("Patient", "name", DeIdentificationStrategy.Remove),
        new("Patient", "telecom", DeIdentificationStrategy.Remove),
        new("Patient", "photo", DeIdentificationStrategy.Remove),
        new("Patient", "contact", DeIdentificationStrategy.Remove),
        new("Patient", "address.line", DeIdentificationStrategy.Remove),
        new("Patient", "address.text", DeIdentificationStrategy.Remove),
        new("Patient", "address.postalCode", DeIdentificationStrategy.GeneralizeZip3),
        new("Patient", "birthDate", DeIdentificationStrategy.GeneralizeDateToYear),
        new("Patient", "identifier.value", DeIdentificationStrategy.Hash),
        new("*", "text", DeIdentificationStrategy.Remove)
    ];

    private sealed record DeIdentificationRule(string ResourceType, string Path, DeIdentificationStrategy Strategy)
    {
        public IReadOnlyList<string> PathSegments { get; } = Path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        public bool AppliesTo(string resourceType)
            => ResourceType == "*" || string.Equals(ResourceType, resourceType, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RuleConfig
    {
        public string? ResourceType { get; set; }
        public string Path { get; set; } = string.Empty;
        public string Strategy { get; set; } = string.Empty;
    }
}

public enum DeIdentificationStrategy
{
    Remove = 0,
    Redact = 1,
    Hash = 2,
    GeneralizeDateToYear = 3,
    GeneralizeZip3 = 4
}
