using FHIRBridge.Domain.Entities.Licensing;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Licensing;

/// <summary>Covers both anti-clock-rollback heuristics described in <see cref="UsageLedgerEntry"/>'s remarks:
/// the cross-restart wall-clock high-water-mark check, and the same-boot monotonic-vs-wall-clock delta
/// check.</summary>
public sealed class ClockRollbackDetectorTests
{
    [Fact]
    public void CrossRestart_check_passes_when_the_clock_advances_normally()
    {
        var lastObservedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var observedUtc = lastObservedUtc.AddDays(1);

        ClockRollbackDetector.IsCrossRestartRollbackSuspected(observedUtc, lastObservedUtc).Should().BeFalse();
    }

    [Fact]
    public void CrossRestart_check_flags_a_new_observation_that_claims_to_be_meaningfully_earlier()
    {
        var lastObservedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var observedUtc = lastObservedUtc.AddDays(-1); // clock wound back a full day

        ClockRollbackDetector.IsCrossRestartRollbackSuspected(observedUtc, lastObservedUtc).Should().BeTrue();
    }

    [Fact]
    public void CrossRestart_check_tolerates_a_small_amount_of_backward_skew()
    {
        var lastObservedUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var observedUtc = lastObservedUtc.AddSeconds(-1); // well within Tolerance (5s)

        ClockRollbackDetector.IsCrossRestartRollbackSuspected(observedUtc, lastObservedUtc).Should().BeFalse();
    }

    [Fact]
    public void SameBoot_check_passes_when_wall_clock_and_monotonic_clock_agree()
    {
        var previousObservedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var previousMonotonicTicks = 100_000L;

        var currentObservedUtc = previousObservedUtc.AddHours(1);
        var currentMonotonicTicks = previousMonotonicTicks + (long)TimeSpan.FromHours(1).TotalMilliseconds;

        ClockRollbackDetector.IsSameBootRollbackSuspected(
                previousObservedUtc, previousMonotonicTicks, currentObservedUtc, currentMonotonicTicks)
            .Should().BeFalse();
    }

    [Fact]
    public void SameBoot_check_flags_wall_clock_lagging_meaningfully_behind_the_monotonic_clock()
    {
        var previousObservedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var previousMonotonicTicks = 100_000L;

        // Real (monotonic) time advanced by an hour, but the wall clock only advanced by one minute —
        // someone wound the OS clock back while the process kept running.
        var currentObservedUtc = previousObservedUtc.AddMinutes(1);
        var currentMonotonicTicks = previousMonotonicTicks + (long)TimeSpan.FromHours(1).TotalMilliseconds;

        ClockRollbackDetector.IsSameBootRollbackSuspected(
                previousObservedUtc, previousMonotonicTicks, currentObservedUtc, currentMonotonicTicks)
            .Should().BeTrue();
    }

    [Fact]
    public void SameBoot_check_does_not_false_positive_across_a_legitimate_restart()
    {
        // A process restart resets Environment.TickCount64 to a small value — even though the wall clock
        // correctly advanced by a full day, the monotonic counter went "backward" from the previous row's
        // perspective. This must never be reported as a rollback; the cross-restart check covers this gap
        // instead.
        var previousObservedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var previousMonotonicTicks = 5_000_000L;

        var currentObservedUtc = previousObservedUtc.AddDays(1);
        var currentMonotonicTicks = 30_000L; // small — process restarted

        ClockRollbackDetector.IsSameBootRollbackSuspected(
                previousObservedUtc, previousMonotonicTicks, currentObservedUtc, currentMonotonicTicks)
            .Should().BeFalse();
    }

    [Fact]
    public void SameBoot_check_tolerates_small_scheduling_jitter()
    {
        var previousObservedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var previousMonotonicTicks = 100_000L;

        // Monotonic delta is 60,000ms; wall delta is 59,000ms (1s of jitter) — well within the 5s Tolerance.
        var currentObservedUtc = previousObservedUtc.AddMilliseconds(59_000);
        var currentMonotonicTicks = previousMonotonicTicks + 60_000;

        ClockRollbackDetector.IsSameBootRollbackSuspected(
                previousObservedUtc, previousMonotonicTicks, currentObservedUtc, currentMonotonicTicks)
            .Should().BeFalse();
    }
}
