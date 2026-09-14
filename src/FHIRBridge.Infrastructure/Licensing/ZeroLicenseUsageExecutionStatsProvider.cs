using FHIRBridge.Application.Abstractions.Licensing;

namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>No-DB dev/test profile stand-in — same reasoning as <see cref="NullUsageLedgerRepository"/>:
/// nothing to count against, so every call reports all-zero stats.</summary>
public sealed class ZeroLicenseUsageExecutionStatsProvider : ILicenseUsageExecutionStatsProvider
{
    public Task<LicenseUsageExecutionStats> GetCurrentStatsAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new LicenseUsageExecutionStats(0, 0, 0, 0));
}
