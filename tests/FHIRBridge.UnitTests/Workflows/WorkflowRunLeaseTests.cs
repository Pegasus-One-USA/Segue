using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Workflows;

/// <summary>
/// PR #240 review: with several API and Worker instances, a run is closed as interrupted, or cancelled outright, only
/// when nothing renews its lease; a run another instance is executing keeps its lease and is cancelled through it.
/// </summary>
public sealed class WorkflowRunLeaseTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static WorkflowRun Run(DateTimeOffset startedAt, DateTimeOffset? leaseExpiresAt, string triggerType = "Manual")
    {
        var run = new WorkflowRun(Guid.NewGuid(), Guid.NewGuid(), startedAt, triggerType: triggerType);
        if (leaseExpiresAt is { } expires)
        {
            run.HoldLease("other-instance", expires);
        }

        return run;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    public void A_run_is_dead_once_its_lease_lapses(int leaseMinutesFromNow, bool expired)
    {
        Run(Now.AddHours(-3), Now.AddMinutes(leaseMinutesFromNow)).IsLeaseExpired(Now, WorkflowRunLease.LegacyGrace).Should().Be(expired);
    }

    [Theory]
    [InlineData(-10, false)]
    [InlineData(-31, true)]
    public void A_run_from_before_leases_is_dead_once_older_than_the_grace(int startedMinutesAgo, bool expired)
    {
        Run(Now.AddMinutes(startedMinutesAgo), leaseExpiresAt: null).IsLeaseExpired(Now, WorkflowRunLease.LegacyGrace).Should().Be(expired);
    }

    [Fact]
    public void A_run_is_held_until_every_holder_lets_go()
    {
        var active = new ActiveWorkflowRuns();
        var id = Guid.NewGuid();

        var first = active.Hold(id);
        var second = active.Hold(id);
        first.Dispose();
        first.Dispose();

        active.Snapshot().Should().Contain(id);
        second.Dispose();
        active.Snapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task Only_runs_whose_lease_lapsed_are_failed()
    {
        var store = new InMemoryWorkflowRunStore();
        var live = Run(Now.AddHours(-2), Now.AddMinutes(1));
        var dead = Run(Now.AddHours(-2), Now.AddMinutes(-1));
        var legacyYoung = Run(Now.AddMinutes(-5), leaseExpiresAt: null);
        foreach (var run in new[] { live, dead, legacyYoung })
        {
            await store.SaveAsync(run, CancellationToken.None);
        }

        var failed = await store.FailExpiredRunsAsync(Now, Now - WorkflowRunLease.LegacyGrace, WorkflowRunLease.InterruptedReason, CancellationToken.None);

        failed.Should().Be(1);
        dead.Status.Should().Be(WorkflowRunStatus.Failed);
        dead.ErrorMessage.Should().Be(WorkflowRunLease.InterruptedReason);
        live.Status.Should().Be(WorkflowRunStatus.Running);
        legacyYoung.Status.Should().Be(WorkflowRunStatus.Running);
    }

    [Fact]
    public async Task A_heartbeat_keeps_this_instances_runs_alive_stops_cancelled_ones_and_closes_dead_ones()
    {
        var store = new InMemoryWorkflowRunStore();
        var tracker = new InMemoryWorkflowRunTracker();
        var active = new ActiveWorkflowRuns();
        var services = new ServiceCollection().AddSingleton<IWorkflowRunStore>(store).BuildServiceProvider();
        var time = new FixedTime(Now);
        var lease = new WorkflowRunLeaseService(
            services.GetRequiredService<IServiceScopeFactory>(), active, tracker, NullLogger<WorkflowRunLeaseService>.Instance, time);

        // Ours, its lease about to lapse; another instance asked to cancel it.
        var ours = Run(Now.AddMinutes(-10), Now.AddSeconds(-1));
        using var ourCancellation = new CancellationTokenSource();
        tracker.MarkRunning(ours.Id, ourCancellation);
        using var held = active.Hold(ours.Id);
        // Nobody's: its process stopped.
        var orphan = Run(Now.AddMinutes(-10), Now.AddSeconds(-1));
        await store.SaveAsync(ours, CancellationToken.None);
        await store.SaveAsync(orphan, CancellationToken.None);
        (await store.RequestCancellationAsync(ours.Id, Now, CancellationToken.None)).Should().BeTrue();

        await lease.TickAsync(CancellationToken.None);

        ours.Status.Should().Be(WorkflowRunStatus.Running);
        ours.LeaseExpiresAt.Should().Be(Now + WorkflowRunLease.Duration);
        ours.LeaseOwner.Should().Be(active.InstanceId);
        ourCancellation.IsCancellationRequested.Should().BeTrue(because: "the cancel was made on another instance");
        orphan.Status.Should().Be(WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task A_finished_run_cannot_be_asked_to_cancel()
    {
        var store = new InMemoryWorkflowRunStore();
        var run = Run(Now, Now.AddMinutes(2));
        run.Fail("x", Now);
        await store.SaveAsync(run, CancellationToken.None);

        (await store.RequestCancellationAsync(run.Id, Now, CancellationToken.None)).Should().BeFalse();
    }

    private sealed class FixedTime : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTime(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
