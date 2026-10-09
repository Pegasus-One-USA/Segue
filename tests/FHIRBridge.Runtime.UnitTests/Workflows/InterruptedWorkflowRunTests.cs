using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

/// <summary>
/// A run left "Running" by a host that stopped is failed by that host at its next start-up, and only by that host:
/// restarting the API must never fail a run the Worker is still executing, nor the other way round.
/// </summary>
public sealed class InterruptedWorkflowRunTests
{
    private static readonly DateTimeOffset HostStarted = new(2026, 10, 5, 15, 30, 0, TimeSpan.Zero);

    private static WorkflowRun Run(string triggerType, DateTimeOffset startedAt) =>
        new(Guid.NewGuid(), Guid.NewGuid(), startedAt, "user-id", triggerType);

    [Theory]
    [InlineData("Manual", WorkflowRunHost.Api)]
    [InlineData("Checkpoint", WorkflowRunHost.Api)]
    [InlineData("InteractiveLaunch", WorkflowRunHost.Api)]
    [InlineData("Scheduled", WorkflowRunHost.Worker)]
    public void Each_trigger_type_belongs_to_the_host_that_executes_it(string triggerType, WorkflowRunHost expected)
    {
        WorkflowRunHosts.HostOf(Run(triggerType, HostStarted)).Should().Be(expected);
    }

    [Fact]
    public void A_run_resumed_after_a_bulk_export_belongs_to_the_worker_whoever_started_it()
    {
        var run = Run("Manual", HostStarted);
        run.AwaitBulkExport("job-1");

        WorkflowRunHosts.HostOf(run).Should().Be(WorkflowRunHost.Worker);
    }

    [Fact]
    public async Task The_api_fails_only_its_own_runs_started_before_it_came_up()
    {
        var store = new InMemoryWorkflowRunStore();
        var orphan = Run("Manual", HostStarted.AddMinutes(-5));
        var startedSince = Run("Manual", HostStarted.AddSeconds(1));
        var workerRun = Run("Scheduled", HostStarted.AddMinutes(-5));
        var finished = Run("Manual", HostStarted.AddMinutes(-9));
        finished.Succeed(HostStarted.AddMinutes(-8));
        foreach (var run in new[] { orphan, startedSince, workerRun, finished })
        {
            await store.SaveAsync(run, CancellationToken.None);
        }

        var count = await store.FailInterruptedRunsAsync(WorkflowRunHost.Api, HostStarted, "Interrupted: the API stopped.", CancellationToken.None);

        count.Should().Be(1);
        orphan.Status.Should().Be(WorkflowRunStatus.Failed);
        orphan.ErrorMessage.Should().Be("Interrupted: the API stopped.");
        orphan.CompletedAt.Should().NotBeNull();
        startedSince.Status.Should().Be(WorkflowRunStatus.Running, because: "this API process may be executing it");
        workerRun.Status.Should().Be(WorkflowRunStatus.Running, because: "the Worker owns it");
        finished.Status.Should().Be(WorkflowRunStatus.Succeeded);
    }

    [Fact]
    public async Task The_worker_fails_only_its_own_runs()
    {
        var store = new InMemoryWorkflowRunStore();
        var scheduled = Run("Scheduled", HostStarted.AddMinutes(-5));
        var manual = Run("Manual", HostStarted.AddMinutes(-5));
        await store.SaveAsync(scheduled, CancellationToken.None);
        await store.SaveAsync(manual, CancellationToken.None);

        var count = await store.FailInterruptedRunsAsync(WorkflowRunHost.Worker, HostStarted, "Interrupted: the Worker stopped.", CancellationToken.None);

        count.Should().Be(1);
        scheduled.Status.Should().Be(WorkflowRunStatus.Failed);
        manual.Status.Should().Be(WorkflowRunStatus.Running);
    }
}
