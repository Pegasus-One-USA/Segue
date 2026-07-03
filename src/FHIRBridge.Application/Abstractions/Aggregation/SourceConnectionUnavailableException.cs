namespace FHIRBridge.Application.Abstractions.Aggregation;

/// <summary>
/// Thrown when a patient aggregation read cannot resolve a usable source connection — there is no
/// enabled source, the requested source is disabled, or there are multiple enabled sources and none was
/// specified to disambiguate. The API layer maps this to a 409 OperationOutcome.
/// </summary>
public sealed class SourceConnectionUnavailableException : Exception
{
    public SourceConnectionUnavailableException(string message)
        : base(message)
    {
    }
}
