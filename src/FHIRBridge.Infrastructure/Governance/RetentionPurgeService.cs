using FHIRBridge.Application.Abstractions.Governance;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Governance;

/// <summary>
/// Acts on the retention policy by purging expired records from every <see cref="IPurgeableStore"/>. Each store's
/// cutoff is resolved from its own policy (<c>GetPolicy(store.DataClass)</c>, falling back to the platform default
/// when no per-data-class override is configured — see <see cref="ConfiguredRetentionPolicyService"/>), so
/// short-lived Operations logs and the platform default don't have to share one retention period. Immutable
/// stores (the HIPAA audit log and its companions) do not implement <see cref="IPurgeableStore"/> and are
/// therefore never touched, honoring <c>IsImmutableAuditRequired</c>.
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
        // Reported as the headline cutoff/years for backward-compatible summary purposes — the platform default,
        // not necessarily what every individual store actually used below.
        var defaultPolicy = _retentionPolicyService.GetPolicy("*");

        var purgedByDataClass = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var store in _purgeableStores)
        {
            var policy = _retentionPolicyService.GetPolicy(store.DataClass);
            var cutoff = nowUtc.AddYears(-policy.RetentionYears);

            var purged = await store.PurgeOlderThanAsync(cutoff, cancellationToken);
            purgedByDataClass[store.DataClass] = purged;

            if (purged > 0)
            {
                _logger.LogInformation(
                    "Retention purge removed {Count} {DataClass} record(s) older than {Cutoff:o} ({Years}y policy).",
                    purged, store.DataClass, cutoff, policy.RetentionYears);
            }
        }

        return new RetentionPurgeReport(
            nowUtc.AddYears(-defaultPolicy.RetentionYears), defaultPolicy.RetentionYears, purgedByDataClass);
    }
}
