using System.Text.Json;
using FHIRBridge.Application.Abstractions.Governance;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Parses a FHIR R4 <c>Consent</c> resource into a <see cref="TenantConsent"/> directive: reads the consent status,
/// the base provision type (permit/deny), and any nested sub-provisions whose <c>class</c> codings name the resource
/// types they apply to (treated as exceptions to the base provision).
/// </summary>
public static class FhirConsentParser
{
    public static TenantConsent Parse(string consentJson)
    {
        using var document = JsonDocument.Parse(consentJson);
        var root = document.RootElement;

        if (!string.Equals(GetString(root, "resourceType"), "Consent", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Resource is not a FHIR Consent.");
        }

        var isActive = string.Equals(GetString(root, "status"), "active", StringComparison.OrdinalIgnoreCase);

        var baseProvision = ConsentProvisionType.Permit;
        var exceptions = new List<string>();

        if (root.TryGetProperty("provision", out var provision) && provision.ValueKind == JsonValueKind.Object)
        {
            baseProvision = ParseProvisionType(GetString(provision, "type"));

            if (provision.TryGetProperty("provision", out var subProvisions) && subProvisions.ValueKind == JsonValueKind.Array)
            {
                foreach (var sub in subProvisions.EnumerateArray())
                {
                    exceptions.AddRange(ReadResourceTypes(sub));
                }
            }
        }

        return new TenantConsent(isActive, baseProvision, exceptions);
    }

    private static IEnumerable<string> ReadResourceTypes(JsonElement provision)
    {
        if (!provision.TryGetProperty("class", out var classes) || classes.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var coding in classes.EnumerateArray())
        {
            var code = GetString(coding, "code");
            if (!string.IsNullOrWhiteSpace(code))
            {
                yield return code;
            }
        }
    }

    private static ConsentProvisionType ParseProvisionType(string? type)
        => string.Equals(type, "deny", StringComparison.OrdinalIgnoreCase)
            ? ConsentProvisionType.Deny
            : ConsentProvisionType.Permit;

    private static string? GetString(JsonElement element, string propertyName)
        => element.ValueKind == JsonValueKind.Object &&
           element.TryGetProperty(propertyName, out var property) &&
           property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
}
