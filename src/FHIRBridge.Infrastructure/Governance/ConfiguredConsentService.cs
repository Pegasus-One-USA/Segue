using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Configuration-driven consent enforcement. Reads a per-tenant consent directive from
/// <c>Governance:Consent:{tenantId}</c> ({ Active, BaseProvision = Permit|Deny, ExceptionResourceTypes: [] }). When a
/// tenant has no consent on file the default is permissive (no restriction configured); a consent that is present but
/// inactive denies delivery, mirroring a withdrawn FHIR Consent.
/// </summary>
public sealed class ConfiguredConsentService : IConsentService
{
    private readonly IReadOnlyDictionary<Guid, TenantConsent> _consentsByTenant;

    public ConfiguredConsentService(IConfiguration configuration)
    {
        _consentsByTenant = LoadConsents(configuration);
    }

    public ConsentDecision Evaluate(Guid tenantId, string resourceType, string? resourceId)
    {
        if (!_consentsByTenant.TryGetValue(tenantId, out var consent))
        {
            return new ConsentDecision(true, null);
        }

        if (consent.Permits(resourceType))
        {
            return new ConsentDecision(true, null);
        }

        var reason = consent.IsActive
            ? $"Consent does not permit access to resource type '{resourceType}'."
            : "No active patient consent is on file.";

        return new ConsentDecision(false, reason);
    }

    private static IReadOnlyDictionary<Guid, TenantConsent> LoadConsents(IConfiguration configuration)
    {
        var result = new Dictionary<Guid, TenantConsent>();
        foreach (var tenantSection in configuration.GetSection("Governance:Consent").GetChildren())
        {
            if (!Guid.TryParse(tenantSection.Key, out var tenantId))
            {
                continue;
            }

            var active = !bool.TryParse(tenantSection["Active"], out var parsedActive) || parsedActive;
            var baseProvision = Enum.TryParse<ConsentProvisionType>(tenantSection["BaseProvision"], true, out var parsed)
                ? parsed
                : ConsentProvisionType.Permit;
            var exceptions = tenantSection.GetSection("ExceptionResourceTypes").Get<string[]>() ?? [];

            result[tenantId] = new TenantConsent(active, baseProvision, exceptions);
        }

        return result;
    }
}
