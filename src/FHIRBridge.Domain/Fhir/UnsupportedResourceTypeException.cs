namespace FHIRBridge.Domain.Fhir;

/// <summary>
/// Thrown when an aggregation <c>include</c> request names a resource type that is not in
/// <see cref="PatientCompartmentResourceTypes"/>. The API layer maps this to a 400 OperationOutcome.
/// </summary>
public sealed class UnsupportedResourceTypeException : Exception
{
    public UnsupportedResourceTypeException(string resourceType)
        : base($"Resource type '{resourceType}' is not supported for patient aggregation.")
    {
        ResourceType = resourceType;
    }

    /// <summary>The offending resource type as supplied by the caller.</summary>
    public string ResourceType { get; }
}
