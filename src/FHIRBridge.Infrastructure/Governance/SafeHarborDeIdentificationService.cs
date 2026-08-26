using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// HIPAA Safe Harbor-style de-identification. Applies the pre-mapping <c>TransformationRule</c> rows belonging
/// to <see cref="DeIdentificationRequest.ProfileId"/> as a set of FHIR-path redaction rules over the raw resource
/// JSON: direct identifiers are removed, hashed, or masked, and dates/ZIPs are generalized. A null
/// <see cref="DeIdentificationRequest.ProfileId"/> means "no profile assigned" — the resource passes through
/// unchanged, since profiles (not one tenant-wide default) are the unit of "what redaction applies here."
/// </summary>
public sealed class SafeHarborDeIdentificationService : IDeIdentificationService
{
    private readonly ITransformationRuleRepository _ruleRepository;
    private readonly ILogger<SafeHarborDeIdentificationService> _logger;

    public SafeHarborDeIdentificationService(
        ITransformationRuleRepository ruleRepository,
        ILogger<SafeHarborDeIdentificationService>? logger = null)
    {
        _ruleRepository = ruleRepository;
        _logger = logger ?? NullLogger<SafeHarborDeIdentificationService>.Instance;
    }

    public async Task<DeIdentificationResult> DeIdentifyAsync(DeIdentificationRequest request, CancellationToken cancellationToken)
    {
        if (request.ProfileId is not { } profileId)
        {
            return new DeIdentificationResult(request.RawJson, []);
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(request.RawJson);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "De-identification skipped: {ResourceType} is not valid JSON.", request.ResourceType);
            return new DeIdentificationResult(request.RawJson, []);
        }

        if (root is not JsonObject resource)
        {
            return new DeIdentificationResult(request.RawJson, []);
        }

        var hops = new List<DeIdentificationFieldHop>();
        var rules = await _ruleRepository.GetPreMappingRulesAsync(profileId, request.ResourceType, cancellationToken);
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.SourceField) || !TryReadStrategy(rule.ConfigJson, out var strategy))
            {
                continue;
            }

            // Captured before mutating so the hop's "before" value reflects what this field actually held prior
            // to this rule — TryReadValueAt returns null both for "path doesn't exist" and "value was itself
            // null," which is fine here: either way there is nothing meaningful to redact or report as changed.
            var pathSegments = rule.SourceField.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var beforeValue = TryReadValueAt(resource, pathSegments, 0);
            if (beforeValue is null)
            {
                continue;
            }

            ApplyPath(resource, pathSegments, 0, strategy, rule.ConfigJson);
            var afterValue = TryReadValueAt(resource, pathSegments, 0);

            hops.Add(new DeIdentificationFieldHop(
                rule.SourceField,
                strategy.ToString(),
                rule.ConfigJson,
                beforeValue,
                afterValue,
                true,
                null));
        }

        return new DeIdentificationResult(resource.ToJsonString(), hops);
    }

    /// <summary>Best-effort read of the same path <see cref="ApplyPath"/> would mutate — used only to capture a
    /// lineage hop's before/after value, so it deliberately mirrors ApplyPath's array-fan-out/object-descent
    /// rules but returns a single serialized snapshot (the first match) rather than mutating every array element.</summary>
    private static string? TryReadValueAt(JsonNode? node, IReadOnlyList<string> path, int index)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var element in array)
                {
                    var value = TryReadValueAt(element, path, index);
                    if (value is not null)
                    {
                        return value;
                    }
                }

                return null;

            case JsonObject obj when index == path.Count - 1:
                return obj.TryGetPropertyValue(path[index], out var leaf) && leaf is not null
                    ? leaf.ToJsonString()
                    : null;

            case JsonObject obj:
                return obj.TryGetPropertyValue(path[index], out var child)
                    ? TryReadValueAt(child, path, index + 1)
                    : null;

            default:
                return null;
        }
    }

    private static bool TryReadStrategy(string configJson, out DeIdentificationStrategy strategy)
    {
        strategy = default;
        try
        {
            using var document = JsonDocument.Parse(configJson);
            if (!document.RootElement.TryGetProperty("mode", out var modeElement) || modeElement.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            return Enum.TryParse(modeElement.GetString(), true, out strategy);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void ApplyPath(JsonNode? node, IReadOnlyList<string> path, int index, DeIdentificationStrategy strategy, string configJson)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var element in array)
                {
                    ApplyPath(element, path, index, strategy, configJson);
                }

                break;

            case JsonObject obj when index == path.Count - 1:
                ApplyStrategy(obj, path[index], strategy, configJson);
                break;

            case JsonObject obj:
                if (obj.TryGetPropertyValue(path[index], out var child))
                {
                    ApplyPath(child, path, index + 1, strategy, configJson);
                }

                break;
        }
    }

    private static void ApplyStrategy(JsonObject parent, string property, DeIdentificationStrategy strategy, string configJson)
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
                parent[property] = ReadConfigString(configJson, "token") ?? "[REDACTED]";
                break;

            case DeIdentificationStrategy.Hash:
                if (current is JsonValue hashValue && hashValue.TryGetValue<string>(out var raw))
                {
                    parent[property] = Hash(raw);
                }

                break;

            case DeIdentificationStrategy.Mask:
                if (current is JsonValue maskValue && maskValue.TryGetValue<string>(out var maskRaw))
                {
                    var keepLength = ReadConfigInt(configJson, "keepLength", 4);
                    parent[property] = Mask(maskRaw, keepLength);
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

    private static string Mask(string value, int keepLength)
        => value.Length <= keepLength ? value : new string('*', value.Length - keepLength) + value[^keepLength..];

    private static string? ReadConfigString(string configJson, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int ReadConfigInt(string configJson, string property, int fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(configJson);
            return document.RootElement.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
                ? parsed
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }
}

public enum DeIdentificationStrategy
{
    Remove = 0,
    Redact = 1,
    Hash = 2,
    GeneralizeDateToYear = 3,
    GeneralizeZip3 = 4,
    Mask = 5
}
