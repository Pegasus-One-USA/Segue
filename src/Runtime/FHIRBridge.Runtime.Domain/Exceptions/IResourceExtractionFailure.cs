namespace FHIRBridge.Runtime.Domain.Exceptions;

/// <summary>
/// Common shape for a source-extraction failure that's isolated to a single <see cref="ResourceType"/> — the
/// caller (<c>SourceNodeExecutors</c>) can skip just this resource type (or cancel the run, if it's the
/// cohort-seeding type) rather than treating it as a fatal, whole-run failure. Implemented by
/// <see cref="ResourceAuthorizationException"/> (401/403), <see cref="ResourceNotSupportedException"/>
/// (400 "not-supported" OperationOutcome), and <see cref="ResourceRequestFailedException"/> (any other
/// non-success response to this resource type's own search request).
/// </summary>
public interface IResourceExtractionFailure
{
    string ResourceType { get; }

    int StatusCode { get; }

    string SkipReasonLabel { get; }
}
