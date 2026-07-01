using FHIRBridge.Runtime.Application.DTOs;

namespace FHIRBridge.Runtime.Application.Abstractions.Connectors;

/// <summary>
/// Manages the lifecycle of rest-hook <c>Subscription</c> resources on a source FHIR server so the server pushes
/// changes to FHIRBridge (the outbound half of FHIR Subscriptions). Complements the inbound webhook ingestion path.
/// </summary>
public interface IFhirSubscriptionClient
{
    Task<FhirSubscriptionRegistration> CreateAsync(
        FhirSubscriptionRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    Task<FhirSubscriptionRegistration> UpdateAsync(
        string subscriptionId,
        FhirSubscriptionRequest request,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        string subscriptionId,
        FhirSourceConfiguration source,
        CancellationToken cancellationToken);
}
