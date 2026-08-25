namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// Thrown when a FHIR Bulk Data <c>$export</c> kick-off would exceed this SourceConnection's configured concurrency
/// cap (see <c>IBulkExportJobRepository.CountActiveBySourceConnectionAsync</c>). The message text deliberately
/// matches the wording FHIR Bulk Data servers commonly return for their own server-side capacity throttling (e.g.
/// athenahealth's 429 "Too many active exports. Please try again later.") so a client app already handling that
/// vendor's error text works unmodified against this client-side pre-check too.
/// </summary>
public sealed class BulkExportConcurrencyLimitExceededException : FHIRBridgeException
{
    public BulkExportConcurrencyLimitExceededException(TimeSpan retryAfter)
        : base("Too many active exports. Please try again later.")
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan RetryAfter { get; }
}
