namespace FHIRBridge.Runtime.Domain.Exceptions;

/// <summary>
/// Thrown by a source connector when a FHIR search for a specific <see cref="ResourceType"/> fails with 401/403 —
/// distinguishes "this app isn't authorized for this resource type" from any other extraction failure (timeout,
/// malformed response, transient 5xx) so callers can decide whether to isolate the failure to just this resource
/// type or treat it as fatal for the whole run. Extends <see cref="InvalidOperationException"/> so existing
/// generic <c>catch (Exception)</c> handling around extraction is unaffected unless a caller specifically checks
/// for this type.
/// </summary>
public sealed class ResourceAuthorizationException : InvalidOperationException, IResourceExtractionFailure
{
    public ResourceAuthorizationException(string resourceType, int statusCode, string reason)
        : base(reason)
    {
        ResourceType = resourceType;
        StatusCode = statusCode;
    }

    public string ResourceType { get; }

    public int StatusCode { get; }

    public string SkipReasonLabel => "not authorized for this app";
}
