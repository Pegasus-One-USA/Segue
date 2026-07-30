using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Persistence;

/// <summary>Persists the permanent user-to-FHIR-context bindings enforced by the interactive OAuth callback.</summary>
public interface IUserFhirContextBindingRepository
{
    /// <summary>Returns the binding for a (sourceConnection, userIdentity) pair, or null if none exists yet.</summary>
    Task<UserFhirContextBinding?> GetAsync(Guid sourceConnectionId, string userIdentity, CancellationToken cancellationToken);

    Task AddAsync(UserFhirContextBinding binding, CancellationToken cancellationToken);
}
