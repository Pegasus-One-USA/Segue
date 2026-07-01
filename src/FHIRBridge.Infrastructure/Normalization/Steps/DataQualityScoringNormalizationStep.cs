using System.Text.Json;
using FHIRBridge.Application.Abstractions.Normalization;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Normalization.Steps;

/// <summary>
/// Phase C — scores each resource for completeness on a 0..1 scale: the fraction of the resource type's key
/// elements that are present and non-empty. The score is surfaced on the result and a warning is emitted when it
/// falls below <see cref="WarnThreshold"/> so downstream systems can flag low-quality records.
/// </summary>
public sealed class DataQualityScoringNormalizationStep : IResourceNormalizationStep
{
    public int Order => 30;

    private const double WarnThreshold = 0.6;

    private readonly ILogger<DataQualityScoringNormalizationStep> _logger;

    public DataQualityScoringNormalizationStep(ILogger<DataQualityScoringNormalizationStep> logger)
    {
        _logger = logger;
    }

    // Key elements that contribute to a complete record per resource type (required + commonly-expected).
    private static readonly IReadOnlyDictionary<string, string[]> ScoredElements =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Patient"] = ["identifier", "name", "gender", "birthDate", "address", "telecom"],
            ["Observation"] = ["status", "code", "subject", "effectiveDateTime", "valueQuantity"],
            ["Condition"] = ["category", "code", "subject", "clinicalStatus", "onsetDateTime"],
            ["MedicationRequest"] = ["status", "intent", "subject", "medicationCodeableConcept", "authoredOn"],
            ["AllergyIntolerance"] = ["clinicalStatus", "code", "patient", "reaction"],
            ["Encounter"] = ["status", "class", "subject", "period", "type"],
            ["DiagnosticReport"] = ["status", "code", "subject", "effectiveDateTime", "result"],
            ["Procedure"] = ["status", "code", "subject", "performedDateTime"],
            ["Immunization"] = ["status", "vaccineCode", "patient", "occurrenceDateTime"],
            // Tier-2 resources.
            ["Coverage"] = ["status", "beneficiary", "payor", "type", "period"],
            ["Claim"] = ["status", "patient", "insurer", "provider", "type"],
            ["ExplanationOfBenefit"] = ["status", "patient", "insurer", "type", "outcome"],
            ["Organization"] = ["identifier", "name", "type", "telecom", "address"],
            ["Practitioner"] = ["identifier", "name", "telecom", "address", "qualification"],
            ["Location"] = ["name", "status", "type", "address", "managingOrganization"],
            // Tier-3 resources.
            ["CarePlan"] = ["status", "intent", "subject", "category", "period"],
            ["Goal"] = ["lifecycleStatus", "description", "subject", "target"],
            ["DocumentReference"] = ["status", "type", "subject", "content", "date"],
            ["Subscription"] = ["status", "criteria", "channel", "reason"],
            ["Group"] = ["type", "actual", "member", "code"],
            ["MeasureReport"] = ["status", "type", "measure", "subject", "period"],
        };

    public Task<ResourceNormalizationResult> ApplyAsync(
        ResourceNormalizationRequest request,
        ResourceNormalizationResult current,
        CancellationToken cancellationToken)
    {
        if (!ScoredElements.TryGetValue(request.ResourceType, out var elements) || elements.Length == 0)
        {
            return Task.FromResult(current);
        }

        double score;
        try
        {
            using var document = JsonDocument.Parse(current.NormalizedJson);
            var root = document.RootElement;
            var present = elements.Count(path => HasNonEmpty(root, path));
            score = Math.Round((double)present / elements.Length, 3);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Data quality scoring skipped: resource {ResourceType} is not valid JSON.", request.ResourceType);
            return Task.FromResult(current);
        }

        var warnings = new List<string>(current.Warnings);
        if (score < WarnThreshold)
        {
            warnings.Add($"Data quality score {score:0.###} for {request.ResourceType} is below threshold {WarnThreshold:0.###}.");
        }

        return Task.FromResult(current with
        {
            DataQualityScore = score,
            Warnings = warnings
        });
    }

    private static bool HasNonEmpty(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Null => false,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(value.GetString()),
            JsonValueKind.Array => value.GetArrayLength() > 0,
            JsonValueKind.Object => value.EnumerateObject().Any(),
            JsonValueKind.Undefined => false,
            _ => true
        };
    }
}
