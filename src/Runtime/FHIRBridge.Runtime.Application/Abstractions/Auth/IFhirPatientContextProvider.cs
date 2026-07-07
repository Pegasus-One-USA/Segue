using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Auth;

/// <summary>
/// Exposes the patient context established by an interactive SMART launch (the <c>patient</c> id returned in the
/// token response, e.g. <c>launch/patient</c>). Source connectors use it to scope a fetch to the launched patient —
/// required because a provider such as Epic rejects an unscoped <c>Patient</c> search ("requires demographics or
/// _id"). Non-interactive grants (Backend Services, client-credentials) have no patient context and return null.
/// </summary>
public interface IFhirPatientContextProvider
{
    Task<string?> GetPatientContextAsync(FhirSourceConfiguration source, CancellationToken cancellationToken);
}
