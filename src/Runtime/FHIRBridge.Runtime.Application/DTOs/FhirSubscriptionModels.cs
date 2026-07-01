namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>
/// Describes a rest-hook FHIR <c>Subscription</c> to register on a source server so it pushes resource changes to
/// FHIRBridge's webhook ingestion endpoint.
/// </summary>
public sealed record FhirSubscriptionRequest(
    string Criteria,
    string CallbackUrl,
    string? Reason = null,
    string PayloadMimeType = "application/fhir+json",
    IReadOnlyCollection<string>? Headers = null);

/// <summary>The result of creating/updating a <c>Subscription</c> on a source FHIR server.</summary>
public sealed record FhirSubscriptionRegistration(
    string Id,
    string Status,
    string RawJson);
