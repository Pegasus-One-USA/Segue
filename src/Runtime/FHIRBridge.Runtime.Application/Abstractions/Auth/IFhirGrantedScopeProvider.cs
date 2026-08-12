using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// Exposes the SMART scope string actually granted by the authorization server for this session — as opposed to
/// <see cref="FhirSourceConfiguration.Scopes"/>, which is only what FHIRBridge itself requested/configured. An IdP
/// can silently narrow the requested scope list (e.g. an interactive user declining a scope on the consent screen,
/// or a backend-services registration the IdP only partially approved); reading the real grant back lets a caller
/// detect that narrowing instead of only ever discovering it via a reactive 401/403 from the FHIR server.
/// </summary>
public interface IFhirGrantedScopeProvider
{
    /// <summary>
    /// Returns the space-delimited scope string the authorization server actually granted for this source's current
    /// session, or null when it isn't known (no token minted/stored yet, or the server didn't echo a <c>scope</c> in
    /// its token response — some IdPs omit it entirely, in which case there is nothing to diff against and callers
    /// should fall back to <see cref="FhirSourceConfiguration.Scopes"/>).
    /// </summary>
    Task<string?> GetGrantedScopeAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);
}
