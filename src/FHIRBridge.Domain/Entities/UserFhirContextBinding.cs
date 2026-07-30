using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>Which FHIR resource type a binding pins a user identity to.</summary>
public enum FhirContextResourceType
{
    Patient,
    Practitioner
}

/// <summary>
/// Permanently pins a third-party app's end-user identity (e.g. a Demo_TestApp account email) to the one FHIR
/// patient/practitioner id their first successful interactive authorization against a given
/// <see cref="SourceConnection"/> returned. Created on first authorization; every later authorization for the same
/// (SourceConnectionId, UserIdentity) pair must return the same ResourceId or is rejected — see
/// InteractiveSourceAuthorizationService.CompleteAsync.
/// </summary>
public sealed class UserFhirContextBinding : Entity<Guid>
{
    private UserFhirContextBinding()
    {
    }

    public UserFhirContextBinding(Guid sourceConnectionId, string userIdentity, FhirContextResourceType resourceType, string resourceId)
    {
        Id = Guid.NewGuid();
        SourceConnectionId = sourceConnectionId;
        UserIdentity = userIdentity;
        ResourceType = resourceType;
        ResourceId = resourceId;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid SourceConnectionId { get; private set; }

    /// <summary>The stable third-party end-user identifier this binding is keyed on (e.g. an account email) —
    /// see LaunchContext.UserIdentity.</summary>
    public string UserIdentity { get; private set; } = default!;

    public FhirContextResourceType ResourceType { get; private set; }

    /// <summary>The bound FHIR resource id (a bare Patient or Practitioner id, not a full reference/URL).</summary>
    public string ResourceId { get; private set; } = default!;

    public DateTimeOffset CreatedUtc { get; private set; }
}
