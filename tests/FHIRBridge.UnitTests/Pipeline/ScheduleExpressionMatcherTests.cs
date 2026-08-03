using FHIRBridge.Infrastructure.Pipeline;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Pipeline;

/// <summary>
/// Cron fields are matched against the schedule's configured time zone, not raw UTC. These cover: a non-UTC zone at
/// an ordinary time, both DST transition boundaries (spring-forward gap, fall-back overlap), and a bad/unknown zone
/// id falling back to UTC instead of throwing.
/// </summary>
public sealed class ScheduleExpressionMatcherTests
{
    private const string Eastern = "America/New_York";

    [Fact]
    public void IsDue_matches_the_configured_zones_wall_clock_time_not_utc()
    {
        // 23:36 UTC on a date Eastern is on standard time (UTC-5) is 18:36 Eastern.
        var utcNow = new DateTime(2026, 1, 15, 23, 36, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDue("36 18 * * *", utcNow, Eastern).Should().BeTrue();
        ScheduleExpressionMatcher.IsDue("36 23 * * *", utcNow, Eastern).Should().BeFalse();
    }

    [Fact]
    public void IsDue_defaults_to_utc_when_no_time_zone_is_supplied()
    {
        var utcNow = new DateTime(2026, 1, 15, 23, 36, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDue("36 23 * * *", utcNow).Should().BeTrue();
    }

    [Fact]
    public void IsDue_shifts_the_effective_utc_instant_across_the_spring_forward_dst_boundary()
    {
        // US Eastern springs forward at 2026-03-08 02:00 local -> 03:00 local (2026-03-08 07:00 UTC).
        // Before the transition, 11:36 PM Eastern is UTC-5 (04:36 UTC next day); after, it's UTC-4 (03:36 UTC).
        var beforeTransitionUtc = new DateTime(2026, 3, 8, 4, 36, 0, DateTimeKind.Utc);
        var afterTransitionUtc = new DateTime(2026, 3, 9, 3, 36, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDue("36 23 * * *", beforeTransitionUtc, Eastern).Should().BeTrue();
        ScheduleExpressionMatcher.IsDue("36 23 * * *", afterTransitionUtc, Eastern).Should().BeTrue();
        // The pre-transition UTC instant no longer matches once the zone has shifted to daylight time.
        ScheduleExpressionMatcher.IsDue("36 23 * * *", beforeTransitionUtc.AddHours(1), Eastern).Should().BeFalse();
    }

    [Fact]
    public void IsDue_handles_the_fall_back_dst_boundary_without_double_firing_the_wrong_hour()
    {
        // US Eastern falls back at 2026-11-01 02:00 local -> 01:00 local (2026-11-01 06:00 UTC).
        // 11:36 PM Eastern is UTC-4 (03:36 UTC) before the fallback and UTC-5 (04:36 UTC) after.
        var beforeFallbackUtc = new DateTime(2026, 11, 1, 3, 36, 0, DateTimeKind.Utc);
        var afterFallbackUtc = new DateTime(2026, 11, 2, 4, 36, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDue("36 23 * * *", beforeFallbackUtc, Eastern).Should().BeTrue();
        ScheduleExpressionMatcher.IsDue("36 23 * * *", afterFallbackUtc, Eastern).Should().BeTrue();
    }

    [Fact]
    public void IsDue_falls_back_to_utc_for_an_unrecognized_time_zone_id_instead_of_throwing()
    {
        var utcNow = new DateTime(2026, 1, 15, 23, 36, 0, DateTimeKind.Utc);

        var act = () => ScheduleExpressionMatcher.IsDue("36 23 * * *", utcNow, "Not/A_Real_Zone");

        act.Should().NotThrow();
        ScheduleExpressionMatcher.IsDue("36 23 * * *", utcNow, "Not/A_Real_Zone").Should().BeTrue();
    }

    [Fact]
    public void IsDueSince_evaluates_each_catch_up_minute_in_the_configured_zone()
    {
        var lastTriggeredUtc = new DateTime(2026, 1, 15, 23, 30, 0, DateTimeKind.Utc);
        var utcNow = new DateTime(2026, 1, 15, 23, 40, 0, DateTimeKind.Utc);

        // 18:36 Eastern (standard time, UTC-5) falls inside the (23:30, 23:40] UTC catch-up window.
        ScheduleExpressionMatcher.IsDueSince("36 18 * * *", lastTriggeredUtc, utcNow, Eastern).Should().BeTrue();
    }

    [Fact]
    public void IsDueSince_catches_up_from_created_on_utc_when_never_triggered()
    {
        // Workflow created at 23:30 UTC with a poll landing at 23:43 — the 23:40 slot must not be missed just
        // because the trigger has never fired before (no LastTriggeredOnUtc yet).
        var createdOnUtc = new DateTime(2026, 1, 15, 23, 30, 0, DateTimeKind.Utc);
        var utcNow = new DateTime(2026, 1, 15, 23, 43, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDueSince("40 23 * * *", null, utcNow, "UTC", createdOnUtc).Should().BeTrue();
    }

    [Fact]
    public void IsDueSince_does_not_backfill_before_created_on_utc_when_never_triggered()
    {
        // A schedule slot that fell before the workflow even existed must not fire retroactively.
        var createdOnUtc = new DateTime(2026, 1, 15, 23, 41, 0, DateTimeKind.Utc);
        var utcNow = new DateTime(2026, 1, 15, 23, 43, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDueSince("40 23 * * *", null, utcNow, "UTC", createdOnUtc).Should().BeFalse();
    }

    [Fact]
    public void IsDueSince_with_no_created_on_utc_falls_back_to_current_minute_only_when_never_triggered()
    {
        var utcNow = new DateTime(2026, 1, 15, 23, 43, 0, DateTimeKind.Utc);

        ScheduleExpressionMatcher.IsDueSince("40 23 * * *", null, utcNow, "UTC", createdOnUtc: null).Should().BeFalse();
    }

    [Fact]
    public void NextDueAfter_returns_a_utc_instant_that_corresponds_to_the_configured_zones_wall_clock_time()
    {
        var fromUtc = new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

        var nextUtc = ScheduleExpressionMatcher.NextDueAfter("36 18 * * *", fromUtc, timeZoneId: Eastern);

        nextUtc.Should().Be(new DateTime(2026, 1, 15, 23, 36, 0, DateTimeKind.Utc));
    }
}
