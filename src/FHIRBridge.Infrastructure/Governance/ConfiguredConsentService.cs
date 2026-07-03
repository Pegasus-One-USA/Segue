using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Configuration-driven consent enforcement. Reads a consent directive from
/// <c>Governance:Consent</c> ({ Active, BaseProvision = Permit|Deny, ExceptionResourceTypes: [] }). When no
/// consent is on file the default is permissive (no restriction configured); a consent that is present but
/// inactive denies delivery, mirroring a withdrawn FHIR Consent.
/// </summary>
public sealed class ConfiguredConsentService : IConsentService
{
    private readonly ConsentDirective? _consent;

    public ConfiguredConsentService(IConfiguration configuration)
    {
        _consent = LoadConsent(configuration);
    }

    public ConsentDecision Evaluate(string resourceType, string? resourceId)
    {
        if (_consent is null)
        {
            return new ConsentDecision(true, null);
        }

        if (_consent.Permits(resourceType))
        {
            return new ConsentDecision(true, null);
        }

        var reason = _consent.IsActive
            ? $"Consent does not permit access to resource type '{resourceType}'."
            : "No active patient consent is on file.";

        return new ConsentDecision(false, reason);
    }

    private static ConsentDirective? LoadConsent(IConfiguration configuration)
    {
        var section = configuration.GetSection("Governance:Consent");
        if (!section.Exists())
        {
            return null;
        }

        var active = !bool.TryParse(section["Active"], out var parsedActive) || parsedActive;
        var baseProvision = Enum.TryParse<ConsentProvisionType>(section["BaseProvision"], true, out var parsed)
            ? parsed
            : ConsentProvisionType.Permit;
        var exceptions = section.GetSection("ExceptionResourceTypes").Get<string[]>() ?? [];

        return new ConsentDirective(active, baseProvision, exceptions);
    }
}
