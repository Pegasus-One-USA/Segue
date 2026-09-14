namespace FHIRBridge.Domain.Entities.Licensing;

/// <summary>
/// Pure, dependency-free heuristics behind the two anti-clock-rollback checks described in
/// <see cref="UsageLedgerEntry"/>'s remarks. Kept as static methods (no repository/EF/clock dependency) so
/// both the correctness of the heuristic and its edge cases (tolerance, restart handling) are directly unit
/// testable without a database.
/// </summary>
public static class ClockRollbackDetector
{
    /// <summary>
    /// Small allowance for ordinary clock drift/NTP correction/scheduling jitter between two consecutive
    /// observations, so a few seconds of harmless skew is never reported as tampering.
    /// </summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cross-restart check (§3a): the ledger itself is the one tamper-evident source of truth, so this
    /// compares a freshly observed wall-clock time against the latest <c>ObservedUtc</c> already recorded in
    /// the ledger. A legitimate clock can only ever move forward between two real observations (beyond the
    /// small <see cref="Tolerance"/> allowance) — if the new observation claims to be meaningfully earlier
    /// than the last recorded one, the system clock was wound back (whether or not the process restarted in
    /// between).
    /// </summary>
    public static bool IsCrossRestartRollbackSuspected(DateTime observedUtc, DateTime lastLedgerObservedUtc) =>
        observedUtc + Tolerance < lastLedgerObservedUtc;

    /// <summary>
    /// Same-boot, no-restart check (§3b): the hash chain alone cannot catch an OS clock change while the
    /// process keeps running (nothing is edited after the fact — every row was written honestly with
    /// whatever the clock said at the time). <see cref="Environment.TickCount64"/>-backed
    /// <paramref name="previousMonotonicTicks"/>/<paramref name="currentMonotonicTicks"/> are unaffected by
    /// wall-clock changes, so real elapsed time is known independently of <see cref="DateTime.UtcNow"/>: if
    /// the monotonic clock says real time advanced by X but the wall clock claims meaningfully less than X
    /// passed, the wall clock was rolled back mid-process.
    ///
    /// Returns <c>false</c> (check does not apply) whenever <paramref name="currentMonotonicTicks"/> has not
    /// strictly advanced past <paramref name="previousMonotonicTicks"/> — that shape means the two
    /// observations don't share the same process boot (the counter resets to a small value on every
    /// restart), a gap <see cref="IsCrossRestartRollbackSuspected"/> covers instead.
    /// </summary>
    public static bool IsSameBootRollbackSuspected(
        DateTime previousObservedUtc,
        long previousMonotonicTicks,
        DateTime currentObservedUtc,
        long currentMonotonicTicks)
    {
        if (currentMonotonicTicks <= previousMonotonicTicks)
        {
            return false;
        }

        var monotonicDelta = TimeSpan.FromMilliseconds(currentMonotonicTicks - previousMonotonicTicks);
        var wallDelta = currentObservedUtc - previousObservedUtc;

        return wallDelta + Tolerance < monotonicDelta;
    }
}
