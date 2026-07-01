namespace FHIRBridge.Application.Abstractions.Governance;

/// <summary>
/// A store whose records are subject to the retention policy and can be purged once expired. Immutable stores (e.g.
/// the HIPAA audit log) deliberately do NOT implement this, so the purge job can never delete them.
/// </summary>
public interface IPurgeableStore
{
    /// <summary>A short name for the kind of data this store holds, used in purge reports/logs.</summary>
    string DataClass { get; }

    /// <summary>Removes records older than the cutoff and returns the number purged.</summary>
    Task<int> PurgeOlderThanAsync(DateTime cutoffUtc, CancellationToken cancellationToken);
}

/// <summary>Runs the retention purge across all purgeable stores and reports what was removed.</summary>
public interface IRetentionPurgeService
{
    Task<RetentionPurgeReport> RunAsync(DateTime nowUtc, CancellationToken cancellationToken);
}

public sealed record RetentionPurgeReport(
    DateTime CutoffUtc,
    int RetentionYears,
    IReadOnlyDictionary<string, int> PurgedByDataClass)
{
    public int TotalPurged => PurgedByDataClass.Values.Sum();
}
