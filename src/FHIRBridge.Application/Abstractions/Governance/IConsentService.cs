namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// Evaluates patient/tenant consent before a resource is delivered. Backed by FHIR <c>Consent</c> resources (parsed
/// into <see cref="TenantConsent"/>) and/or configuration. When no consent is on file the default is permissive; an
/// inactive (withdrawn) consent denies, and an active consent's provisions decide per resource type.
/// </summary>
public interface IConsentService
{
    ConsentDecision Evaluate(string resourceType, string? resourceId);
}

public enum ConsentProvisionType
{
    Permit = 0,
    Deny = 1
}

/// <summary>A consent directive in effect for a tenant.</summary>
public sealed record TenantConsent(
    bool IsActive,
    ConsentProvisionType BaseProvision,
    IReadOnlyCollection<string> ExceptionResourceTypes)
{
    /// <summary>Resolves whether a resource type is permitted under this consent.</summary>
    public bool Permits(string resourceType)
    {
        if (!IsActive)
        {
            return false; // withdrawn / not-yet-active consent denies delivery
        }

        var isException = ExceptionResourceTypes.Contains(resourceType, StringComparer.OrdinalIgnoreCase);

        // Base permit with deny exceptions, or base deny with permit exceptions.
        return BaseProvision == ConsentProvisionType.Permit ? !isException : isException;
    }
}

public sealed record ConsentDecision(bool IsPermitted, string? Reason);
