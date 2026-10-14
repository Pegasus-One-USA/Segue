namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Resolves FHIRBridge roles for an identity from the external IdP that authenticated it, so the IdP —
/// not the local database — is the source of RBAC. Implementations are registered as a collection; the SSO
/// login picks the first one whose <see cref="AppliesToAsync"/> is true (registry/strategy, no switch).
/// </summary>
public interface IExternalRoleResolver
{
    Task<bool> AppliesToAsync(ExternalIdentity identity, CancellationToken cancellationToken);

    Task<ExternalRoleResolution> ResolveAsync(ExternalIdentity identity, CancellationToken cancellationToken);
}
