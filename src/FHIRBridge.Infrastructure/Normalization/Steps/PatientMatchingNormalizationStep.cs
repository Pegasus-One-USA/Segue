using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Normalization;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Normalization.Steps;

/// <summary>
/// Phase E — Master Patient Index (MPI) matching. When a FHIR <c>Patient/$match</c> endpoint is configured, Patient
/// resources are resolved against it to obtain a canonical master id; otherwise (or when $match has no confident
/// match) it falls back to a deterministic <c>MasterPatientId</c> derived from the strongest patient identifier. For
/// Patient resources the identifier comes from <c>identifier[]</c>; for clinical resources from the
/// <c>subject</c>/<c>patient</c> reference. The deterministic id is a hash of <c>{system}|{value}</c> — never the raw
/// identifier — so it is safe to persist and reproducible across runs and instances.
/// </summary>
public sealed class PatientMatchingNormalizationStep : IResourceNormalizationStep
{
    public int Order => 40;

    private readonly ILogger<PatientMatchingNormalizationStep> _logger;
    private readonly IPatientMatchService? _patientMatchService;

    public PatientMatchingNormalizationStep(
        ILogger<PatientMatchingNormalizationStep> logger,
        IPatientMatchService? patientMatchService = null)
    {
        _logger = logger;
        _patientMatchService = patientMatchService;
    }

    // Identifier systems ranked strongest-first for deterministic matching.
    private static readonly string[] PreferredSystems =
    [
        "http://hl7.org/fhir/sid/us-ssn",
        "urn:oid:2.16.840.1.113883.4.1", // SSN OID
        "http://hl7.org/fhir/sid/us-mbi", // Medicare Beneficiary Identifier
    ];

    public async Task<ResourceNormalizationResult> ApplyAsync(
        ResourceNormalizationRequest request,
        ResourceNormalizationResult current,
        CancellationToken cancellationToken)
    {
        var isPatient = string.Equals(request.ResourceType, "Patient", StringComparison.OrdinalIgnoreCase);

        // Prefer a real FHIR Patient/$match against an MPI when one is configured (Patient resources only).
        if (isPatient && _patientMatchService is { IsEnabled: true })
        {
            try
            {
                var matchedId = await _patientMatchService.MatchAsync(current.NormalizedJson, cancellationToken);
                if (!string.IsNullOrWhiteSpace(matchedId))
                {
                    return current with { MasterPatientId = $"mpi-{matchedId}" };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Patient $match failed; using deterministic matching.");
            }
        }

        string? identityKey;
        try
        {
            using var document = JsonDocument.Parse(current.NormalizedJson);
            var root = document.RootElement;

            identityKey = isPatient
                ? ResolvePatientIdentity(root)
                : ResolveReferenceIdentity(root);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Patient matching skipped: resource {ResourceType} is not valid JSON.", request.ResourceType);
            return current;
        }

        if (string.IsNullOrWhiteSpace(identityKey))
        {
            var warnings = new List<string>(current.Warnings)
            {
                $"Patient matching: no usable patient identifier found on {request.ResourceType}."
            };
            return current with { Warnings = warnings };
        }

        return current with { MasterPatientId = Hash(identityKey) };
    }

    // Picks the strongest identifier from a Patient resource: a preferred system if present, else the first.
    private static string? ResolvePatientIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("identifier", out var identifiers) ||
            identifiers.ValueKind != JsonValueKind.Array ||
            identifiers.GetArrayLength() == 0)
        {
            return null;
        }

        string? firstKey = null;

        foreach (var preferred in PreferredSystems)
        {
            foreach (var identifier in identifiers.EnumerateArray())
            {
                var system = GetString(identifier, "system");
                var value = GetString(identifier, "value");
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                firstKey ??= $"{system}|{value}";

                if (string.Equals(system, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    return $"{system}|{value}";
                }
            }
        }

        return firstKey;
    }

    // Uses the subject/patient reference as the matching key for clinical resources.
    private static string? ResolveReferenceIdentity(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in new[] { "subject", "patient" })
        {
            if (root.TryGetProperty(property, out var reference) &&
                reference.ValueKind == JsonValueKind.Object)
            {
                var value = GetString(reference, "reference") ?? GetString(reference, "identifier");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return $"ref|{value}";
                }
            }
        }

        // Provenance has no subject/patient field — it links to a patient only indirectly via target[].
        if (root.TryGetProperty("target", out var targets) && targets.ValueKind == JsonValueKind.Array)
        {
            foreach (var target in targets.EnumerateArray())
            {
                var value = GetString(target, "reference");
                if (value is not null && value.StartsWith("Patient/", StringComparison.OrdinalIgnoreCase))
                {
                    return $"ref|{value}";
                }
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(property, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Stable, non-reversible id so the same identity always maps to the same MasterPatientId without storing PII.
    private static string Hash(string identityKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(identityKey.Trim().ToUpperInvariant()));
        return "mpi-" + Convert.ToHexString(bytes)[..32].ToLowerInvariant();
    }
}
