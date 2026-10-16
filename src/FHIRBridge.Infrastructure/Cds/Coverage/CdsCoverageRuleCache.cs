using FHIRBridge.Application.Abstractions.Cds;
using FHIRBridge.Application.Abstractions.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace FHIRBridge.Infrastructure.Cds.Coverage;

/// <summary>
/// The in-memory payer / rule snapshot behind coverage requirements calls, so a CDS call never waits on a database
/// read of the rules. Rebuilt on first use after <see cref="Invalidate"/> (every admin change on this node calls it)
/// and at most <see cref="MaxAge"/> after the last build, so changes made on another node arrive within a minute.
/// Singleton; reads the repositories through a fresh scope.
/// </summary>
public sealed class CdsCoverageRuleCache : ICdsCoverageRuleCache
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile Entry? _entry;
    private long _version;

    public CdsCoverageRuleCache(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<CdsCoverageRuleSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var entry = _entry;
        if (entry is not null && entry.Version == Interlocked.Read(ref _version) && DateTime.UtcNow - entry.BuiltUtc < MaxAge)
        {
            return entry.Snapshot;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            entry = _entry;
            var version = Interlocked.Read(ref _version);
            if (entry is not null && entry.Version == version && DateTime.UtcNow - entry.BuiltUtc < MaxAge)
            {
                return entry.Snapshot;
            }

            using var scope = _scopeFactory.CreateScope();
            var payers = await scope.ServiceProvider.GetRequiredService<ICdsPayerRepository>().GetAllAsync(cancellationToken);
            var rules = await scope.ServiceProvider.GetRequiredService<ICdsCoverageRuleRepository>().GetAllAsync(cancellationToken);
            var snapshot = new CdsCoverageRuleSnapshot(payers, rules);
            _entry = new Entry(snapshot, version, DateTime.UtcNow);
            return snapshot;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Invalidate() => Interlocked.Increment(ref _version);

    private sealed record Entry(CdsCoverageRuleSnapshot Snapshot, long Version, DateTime BuiltUtc);
}
