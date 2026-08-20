namespace FHIRBridge.Runtime.Domain.Exceptions;

/// <summary>
/// Thrown by a source connector when a FHIR search for a specific <see cref="ResourceType"/> fails with any other
/// non-success HTTP status this connector doesn't already have a more specific exception for (see
/// <see cref="ResourceAuthorizationException"/> for 401/403, <see cref="ResourceNotSupportedException"/> for a
/// "not-supported" <c>OperationOutcome</c>) — e.g. a 400 because the search parameters configured for this
/// resource type don't satisfy what the server requires (a Patient-identifier search criteria reused verbatim
/// against <c>Appointment</c>/<c>Practitioner</c>, which need their own parameter shape). The failure is isolated
/// to this one resource type's search request; it says nothing about whether any other configured resource type's
/// search would also fail, so it's exactly as isolable as the other two <see cref="IResourceExtractionFailure"/>
/// implementers. Deliberately narrower than catching every possible exception here — a genuine transport failure
/// (timeout, DNS, connection reset) throws before a response even exists and never reaches this type; only a real,
/// specific HTTP error response from the server for this resource type's request lands here.
/// </summary>
public sealed class ResourceRequestFailedException : InvalidOperationException, IResourceExtractionFailure
{
    public ResourceRequestFailedException(string resourceType, int statusCode, string reason)
        : base(reason)
    {
        ResourceType = resourceType;
        StatusCode = statusCode;
    }

    public string ResourceType { get; }

    public int StatusCode { get; }

    public string SkipReasonLabel => "request failed";
}
