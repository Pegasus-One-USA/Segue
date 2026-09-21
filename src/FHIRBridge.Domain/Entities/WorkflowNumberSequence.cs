namespace FHIRBridge.Domain.Entities;

/// <summary>
/// The last workflow number issued for one numbering period (see WorkflowNumberPeriod.KeyFor) — the durable
/// counter behind <c>WLW-150926-0042</c>.
/// </summary>
/// <remarks>
/// Deliberately a table rather than a SQL sequence or an in-process counter:
/// <list type="bullet">
/// <item>A static in-memory counter (the approach ErrorReference takes for ERR- ids) is per-process, so two API
/// instances hand out the same number, and a restart replays numbers already persisted. ErrorReference tolerates
/// that by scrambling its suffix; a user-facing "incremental number" cannot.</item>
/// <item>A SQL sequence can't be reset per period without DDL, and this solution ships two migration providers
/// (SQL Server and PostgreSQL) that would each need their own dialect of it.</item>
/// </list>
/// One row per period key, updated under the caller's transaction, keeps allocation gap-free and correct across
/// instances on both providers. Global, not tenant-scoped — matching WorkflowDefinition, which has no TenantId.
/// </remarks>
public sealed class WorkflowNumberSequence
{
    private WorkflowNumberSequence()
    {
        PeriodKey = string.Empty;
    }

    public WorkflowNumberSequence(string periodKey, long lastValue)
    {
        Id = Guid.NewGuid();
        PeriodKey = periodKey;
        LastValue = lastValue;
        UpdatedOnUtc = DateTime.UtcNow;
    }

    public Guid Id { get; private set; }

    /// <summary>Policy-prefixed period identifier, e.g. "daily:2026-09-15". Unique — it is what makes a new
    /// period start at 1 without anything having to reset it.</summary>
    public string PeriodKey { get; private set; }

    /// <summary>Highest number issued for this period so far; the next allocation is this plus one.</summary>
    public long LastValue { get; private set; }

    public DateTime UpdatedOnUtc { get; private set; }

    /// <summary>Optimistic-concurrency guard. Two instances allocating from the same period row concurrently
    /// means the loser's SaveChanges throws DbUpdateConcurrencyException and the allocator retries — see
    /// WorkflowNumberGenerator.NextAsync.</summary>
    public byte[]? RowVersion { get; private set; }

    /// <summary>Consumes and returns the next value in this period.</summary>
    public long Advance()
    {
        LastValue++;
        UpdatedOnUtc = DateTime.UtcNow;
        return LastValue;
    }
}
