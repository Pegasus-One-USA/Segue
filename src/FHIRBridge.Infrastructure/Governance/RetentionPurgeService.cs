using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Acts on the retention policy by purging expired records from every <see cref="IPurgeableStore"/>. The cutoff is
/// <c>now - RetentionYears</c> from the platform-default policy. Immutable stores (the HIPAA audit log) do not
/// implement <see cref="IPurgeableStore"/> and are therefore never touched, honoring <c>IsImmutableAuditRequired</c>.
/// </summary>
public sealed class RetentionPurgeService : IRetentionPurgeService
{
    private readonly IRetentionPolicyService _retentionPolicyService;
    private readonly IReadOnlyList<IPurgeableStore> _purgeableStores;
    private readonly ILogger<RetentionPurgeService> _logger;

    public RetentionPurgeService(
        IRetentionPolicyService retentionPolicyService,
        IEnumerable<IPurgeableStore> purgeableStores,
        ILogger<RetentionPurgeService> logger)
    {
        _retentionPolicyService = retentionPolicyService;
        _purgeableStores = purgeableStores.ToList();
        _logger = logger;
    }

    public async Task<RetentionPurgeReport> RunAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        // Use the platform-default policy (tenant-agnostic) for the store-wide cutoff.
        var policy = _retentionPolicyService.GetPolicy(Guid.Empty, "*");
        var cutoff = nowUtc.AddYears(-policy.RetentionYears);

        var purgedByDataClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in _purgeableStores)
        {
            var purged = await store.PurgeOlderThanAsync(cutoff, cancellationToken);
            purgedByDataClass[store.DataClass] = purged;

            if (purged > 0)
            {
                _logger.LogInformation(
                    "Retention purge removed {Count} {DataClass} record(s) older than {Cutoff:o}.",
                    purged, store.DataClass, cutoff);
            }
        }

        return new RetentionPurgeReport(cutoff, policy.RetentionYears, purgedByDataClass);
    }
}
