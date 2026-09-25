using FHIRBridge.Application.Abstractions.Destinations;
using FluentAssertions;

namespace FHIRBridge.UnitTests.Destinations;

/// <summary>
/// The helper writers use to report their connect stage. It runs <em>inside</em> a writer's own try blocks, so
/// its central obligation is that it never changes the outcome of the write — not on success, not on failure,
/// and not when the reporting itself breaks.
/// </summary>
public sealed class DestinationStageReportingTests
{
    private static PipelineWriteContext Context(
        Func<DestinationStageReport, CancellationToken, Task>? reportStageAsync) =>
        new(false, "Nightly Load", DateTimeOffset.UtcNow, CorrelationId: "corr-1",
            ReportStageAsync: reportStageAsync);

    private static (PipelineWriteContext Context, List<DestinationStageReport> Reports) CapturingContext()
    {
        var reports = new List<DestinationStageReport>();
        return (Context((report, _) =>
        {
            reports.Add(report);
            return Task.CompletedTask;
        }), reports);
    }

    [Fact]
    public async Task A_successful_connect_is_reported_and_its_result_returned()
    {
        var (context, reports) = CapturingContext();

        var connection = await context.ReportConnectAsync(
            () => Task.FromResult("open"), CancellationToken.None, detail: "Warehouse SQL");

        connection.Should().Be("open");
        var report = reports.Should().ContainSingle().Subject;
        report.Stage.Should().Be("Connect");
        report.Status.Should().Be("Succeeded");
        report.Detail.Should().Be("Warehouse SQL");
        report.Error.Should().BeNull();
    }

    [Fact]
    public async Task A_failed_connect_is_reported_with_its_reason_and_still_throws()
    {
        var (context, reports) = CapturingContext();

        var act = async () => await context.ReportConnectAsync<string>(
            () => throw new InvalidOperationException("Login failed for user 'svc'."),
            CancellationToken.None,
            detail: "Warehouse SQL");

        await act.Should().ThrowAsync<InvalidOperationException>();

        var report = reports.Should().ContainSingle().Subject;
        report.Status.Should().Be("Failed");
        report.Error.Should().Be("Login failed for user 'svc'.");
        report.Detail.Should().Be("Warehouse SQL");
    }

    [Fact]
    public async Task A_reporting_failure_never_surfaces_on_a_successful_connect()
    {
        var context = Context((_, _) => throw new InvalidOperationException("governance table is gone"));

        var connection = await context.ReportConnectAsync(
            () => Task.FromResult("open"), CancellationToken.None);

        connection.Should().Be("open");
    }

    [Fact]
    public async Task A_reporting_failure_does_not_mask_the_connects_own_exception()
    {
        var context = Context((_, _) => throw new InvalidOperationException("governance table is gone"));

        var act = async () => await context.ReportConnectAsync<string>(
            () => throw new TimeoutException("the real failure"), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("the real failure");
    }

    [Fact]
    public async Task Without_a_hook_the_connect_still_runs()
    {
        // The default context, and every caller that hasn't opted in — the back-compat guarantee.
        var context = Context(reportStageAsync: null);

        var connection = await context.ReportConnectAsync(
            () => Task.FromResult("open"), CancellationToken.None);

        connection.Should().Be("open");
    }

    [Fact]
    public async Task Without_a_hook_a_failing_connect_still_throws_unchanged()
    {
        var context = Context(reportStageAsync: null);

        var act = async () => await context.ReportConnectAsync<string>(
            () => throw new TimeoutException("boom"), CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>().WithMessage("boom");
    }

    [Fact]
    public async Task An_already_completed_connect_can_be_reported_for_lazily_connecting_clients()
    {
        var (context, reports) = CapturingContext();

        await context.ReportConnectedAsync(
            System.Diagnostics.Stopwatch.GetTimestamp(), CancellationToken.None, detail: "OneLake");

        var report = reports.Should().ContainSingle().Subject;
        report.Stage.Should().Be("Connect");
        report.Status.Should().Be("Succeeded");
        report.Detail.Should().Be("OneLake");
    }

    [Fact]
    public async Task Reporting_an_already_completed_connect_swallows_a_reporting_failure()
    {
        var context = Context((_, _) => throw new InvalidOperationException("governance table is gone"));

        var act = async () => await context.ReportConnectedAsync(
            System.Diagnostics.Stopwatch.GetTimestamp(), CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
