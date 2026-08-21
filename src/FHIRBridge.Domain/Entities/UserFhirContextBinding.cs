using FHIRBridge.SharedKernel.Abstractions;

namespace FHIRBridge.Domain.Entities;

/// <summary>Which FHIR resource type a binding pins a user identity to.</summary>
public enum FhirContextResourceType
{
    Patient,
    Practitioner
}

/// <summary>
/// Permanently pins an identity to the one FHIR patient/practitioner id its first successful interactive
/// authorization against a given <see cref="SourceConnection"/> returned. Created on first authorization; every
/// later authorization for the same (SourceConnectionId, UserIdentity) pair must return the same ResourceId or is
/// rejected — see InteractiveSourceAuthorizationService.CompleteAsync.
///
/// Two shapes, depending on ApplicationType (see InteractiveSourceAuthorizationService.
/// EnforceUserFhirContextBindingAsync for which one applies to which):
/// - Provider Standalone / Patient portal: UserIdentity is the third-party app's own end-user identity (e.g. a
///   Demo_TestApp account email); ResourceId is the Patient/Practitioner id that identity is pinned to.
///   CallerIdentity is unused (null) here — it would just duplicate UserIdentity.
/// - Provider EHR Launch: UserIdentity is "{issuer}:{patient}" instead — the binding is inverted, since a provider
///   is expected to launch into many different patients (pinning one caller to one patient forever would be
///   wrong). ResourceType/ResourceId still hold the real FHIR context (Patient, the same patient id already in
///   UserIdentity); CallerIdentity holds whichever caller identity first established this (issuer, patient) pair,
///   and is what a later authorization's caller identity is compared against.
/// </summary>
public sealed class UserFhirContextBinding : Entity<Guid>
{
    private UserFhirContextBinding()
    {
    }

    public UserFhirContextBinding(
        Guid sourceConnectionId, string userIdentity, FhirContextResourceType resourceType, string resourceId,
        string? callerIdentity = null, string? callerFhirUserId = null)
    {
        Id = Guid.NewGuid();
        SourceConnectionId = sourceConnectionId;
        UserIdentity = userIdentity;
        ResourceType = resourceType;
        ResourceId = resourceId;
        CallerIdentity = callerIdentity;
        CallerFhirUserId = callerFhirUserId;
        CreatedUtc = DateTimeOffset.UtcNow;
    }

    public Guid SourceConnectionId { get; private set; }

    /// <summary>The stable identifier this binding is keyed on — see LaunchContext.UserIdentity and this class's
    /// own remarks for how its shape differs between ApplicationType.EhrLaunch and everything else.</summary>
    public string UserIdentity { get; private set; } = default!;

    public FhirContextResourceType ResourceType { get; private set; }

    /// <summary>The bound FHIR resource id (a bare Patient or Practitioner id, not a full reference/URL).</summary>
    public string ResourceId { get; private set; } = default!;

    /// <summary>ApplicationType.EhrLaunch only: the caller identity (e.g. a Demo_TestApp account email, or its
    /// hardcoded embedded-launch fallback) that first established this (issuer, patient) binding — null for every
    /// other application type (UserIdentity already serves that role there). Kept purely for human-readable
    /// display; see CallerFhirUserId for the value actually used in the mismatch comparison when available.</summary>
    public string? CallerIdentity { get; private set; }

    /// <summary>ApplicationType.EhrLaunch only: the id_token's fhirUser-derived Practitioner id, when Epic returned
    /// one — the actual logged-in Hyperspace clinician, arriving via the token exchange itself rather than
    /// anything the launching app's own session/cookies can supply, so it's reliable even from inside a
    /// third-party-cookie-blocked embedded iframe where CallerIdentity's email is not. Preferred over
    /// CallerIdentity for the mismatch comparison whenever both the stored and the new authorization have one;
    /// falls back to comparing CallerIdentity when either side lacks it (e.g. an EHR that never sends fhirUser).</summary>
    public string? CallerFhirUserId { get; private set; }

    public DateTimeOffset CreatedUtc { get; private set; }
}
