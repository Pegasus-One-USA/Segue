using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Persists the permanent user-to-FHIR-context bindings enforced by the interactive OAuth callback.</summary>
public interface IUserFhirContextBindingRepository
{
    /// <summary>Returns the binding for a (sourceConnection, userIdentity) pair, or null if none exists yet.</summary>
    Task<UserFhirContextBinding?> GetAsync(Guid sourceConnectionId, string userIdentity, CancellationToken cancellationToken);

    /// <summary>
    /// Provider Standalone / Patient portal only: the reverse-direction lookup for the symmetric 1:1 rule — is
    /// this (resourceType, resourceId) already bound to SOME identity (any UserIdentity), regardless of which one?
    /// Used to reject a different account claiming a patient/practitioner another account already owns, on top of
    /// the forward GetAsync check (an account can't claim a different patient/practitioner than it already owns).
    /// Returns null if no binding exists for this resource yet.
    /// </summary>
    Task<UserFhirContextBinding?> GetByResourceAsync(Guid sourceConnectionId, FhirContextResourceType resourceType, string resourceId, CancellationToken cancellationToken);

    Task AddAsync(UserFhirContextBinding binding, CancellationToken cancellationToken);
}
