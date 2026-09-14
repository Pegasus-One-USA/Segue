namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// Thrown when creating a new row (a user, a workflow/route, a source connection) would push a counted
/// license dimension (see <c>LicenseLimits</c>) at or past its configured cap. Mirrors
/// <see cref="BulkExportConcurrencyLimitExceededException"/>'s shape exactly: a plain <see cref="FHIRBridgeException"/>
/// subtype carrying enough structured detail (<see cref="QuotaName"/>/<see cref="Limit"/>/<see cref="CurrentCount"/>)
/// for the mapping layer and any future telemetry to key off, plus an author-written, customer-facing
/// <see cref="FHIRBridgeException.UserMessage"/>.
/// </summary>
public sealed class LicenseQuotaExceededException : FHIRBridgeException
{
    public LicenseQuotaExceededException(string quotaName, int limit, int currentCount)
        : base(
            $"License quota exceeded for '{quotaName}': limit={limit}, currentCount={currentCount}.",
            $"Your license allows a maximum of {limit} {quotaName}; this install already has {currentCount}.")
    {
        QuotaName = quotaName;
        Limit = limit;
        CurrentCount = currentCount;
    }

    /// <summary>The dimension name this quota applies to (e.g. "users", "workflows", "source connections").</summary>
    public string QuotaName { get; }

    /// <summary>The license's configured cap for this dimension (never <c>LicenseLimits.Unlimited</c> — an
    /// unlimited dimension never throws this).</summary>
    public int Limit { get; }

    /// <summary>The live count at the moment this was thrown (before the attempted create would have landed).</summary>
    public int CurrentCount { get; }
}
