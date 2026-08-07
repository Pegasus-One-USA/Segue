namespace FHIRBridge.Runtime.Domain.Exceptions;

/// <summary>
/// Thrown by a source connector when a FHIR search for a specific <see cref="ResourceType"/> fails with a 400
/// carrying a FHIR <c>OperationOutcome</c> "not-supported" issue — e.g. Epic returning
/// <c>"The resource or profile is not supported."</c> because the tenant's app registration doesn't expose that
/// resource type. Distinguishes "this source doesn't have this resource type" from any other extraction failure
/// so callers can isolate the failure to just this resource type instead of failing the whole run. Extends
/// <see cref="InvalidOperationException"/> so existing generic <c>catch (Exception)</c> handling around
/// extraction is unaffected unless a caller specifically checks for this type.
/// </summary>
public sealed class ResourceNotSupportedException : InvalidOperationException, IResourceExtractionFailure
{
    public ResourceNotSupportedException(string resourceType, int statusCode, string reason)
        : base(reason)
    {
        ResourceType = resourceType;
        StatusCode = statusCode;
    }

    public string ResourceType { get; }

    public int StatusCode { get; }

    public string SkipReasonLabel => "not supported by this source";
}
