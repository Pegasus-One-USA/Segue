using FHIRBridge.Application.Services.Workflows.Numbering;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Workflows;

// The period key IS the reset feature: a new period is a key with no counter row yet, so it starts at 1 on
// its own. These tests pin the boundaries where a key must (and must not) change.
public sealed class WorkflowNumberPeriodTests
{
    [Fact]
    public void Daily_key_changes_at_midnight_and_is_stable_within_the_day()
    {
        var morning = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Daily, new DateTime(2026, 9, 15, 0, 0, 1));
        var evening = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Daily, new DateTime(2026, 9, 15, 23, 59, 59));
        var nextDay = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Daily, new DateTime(2026, 9, 16, 0, 0, 0));

        morning.Should().Be(evening);
        nextDay.Should().NotBe(morning);
    }

    [Theory]
    [InlineData(1, "2026-Q1")]
    [InlineData(3, "2026-Q1")]
    [InlineData(4, "2026-Q2")]
    [InlineData(9, "2026-Q3")]
    [InlineData(10, "2026-Q4")]
    [InlineData(12, "2026-Q4")]
    public void Quarterly_key_follows_calendar_quarters(int month, string expectedSuffix)
    {
        WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Quarterly, new DateTime(2026, month, 1))
            .Should().Be($"quarterly:{expectedSuffix}");
    }

    [Fact]
    public void Never_key_is_constant_so_the_counter_never_restarts()
    {
        var early = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Never, new DateTime(2026, 1, 1));
        var muchLater = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Never, new DateTime(2031, 12, 31));

        early.Should().Be(muchLater);
    }

    [Fact]
    public void Policies_never_share_a_key_so_switching_policy_cannot_resume_another_sequence()
    {
        var moment = new DateTime(2026, 4, 1);

        var keys = new[]
        {
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Never, moment),
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Daily, moment),
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Monthly, moment),
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Quarterly, moment),
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.Yearly, moment),
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, moment, "04-01"),
        };

        keys.Should().OnlyHaveUniqueItems();
    }

    // The anchored cycle is the reason CustomAnchorDate exists: a fiscal year that opens on 1 April must keep
    // ONE key from April through the following March, crossing the calendar-year boundary without resetting.
    [Fact]
    public void Anchor_cycle_spans_the_calendar_year_boundary()
    {
        var justAfterAnchor = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2026, 4, 1), "04-01");
        var december = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2026, 12, 31), "04-01");
        var january = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2027, 1, 1), "04-01");
        var justBeforeNextAnchor = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2027, 3, 31), "04-01");
        var nextCycle = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2027, 4, 1), "04-01");

        december.Should().Be(justAfterAnchor);
        january.Should().Be(justAfterAnchor, "a January date belongs to the cycle that opened the previous April");
        justBeforeNextAnchor.Should().Be(justAfterAnchor);
        nextCycle.Should().NotBe(justAfterAnchor, "the anniversary opens a new cycle");
    }

    [Fact]
    public void Anchor_of_29_February_resolves_in_a_non_leap_year_instead_of_throwing()
    {
        var act = () => WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2027, 6, 1), "02-29");

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("13-45")]
    public void Unparseable_anchor_falls_back_to_the_default_rather_than_blocking_creation(string? anchor)
    {
        var fallback = WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2026, 6, 1), anchor);

        fallback.Should().Be(
            WorkflowNumberPeriod.KeyFor(WorkflowNumberResetPolicy.CustomAnchorDate, new DateTime(2026, 6, 1), WorkflowNumberPeriod.DefaultAnchorDate));
    }
}
