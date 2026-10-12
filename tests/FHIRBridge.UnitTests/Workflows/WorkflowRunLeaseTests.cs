using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

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

    // ---- PR #244 review, round 2: the process running a run stops before its lease can lapse -----------------------

    [Fact]
    public void A_run_is_fenced_a_margin_before_its_confirmed_lease_lapses_and_at_once_when_revoked()
    {
        var active = new ActiveWorkflowRuns();
        var id = Guid.NewGuid();
        using var held = active.Hold(id);

        active.HoldsLease(id, Now).Should().BeTrue(because: "a run never confirmed has no lease anyone could see lapse");

        active.Confirm([id], Now + WorkflowRunLease.Duration);
        active.HoldsLease(id, Now + WorkflowRunLease.Duration - WorkflowRunLease.FenceMargin - TimeSpan.FromSeconds(1)).Should().BeTrue();
        active.HoldsLease(id, Now + WorkflowRunLease.Duration - WorkflowRunLease.FenceMargin).Should().BeFalse(
            because: "the process stops before another instance may close the run");

        active.Confirm([id], Now + TimeSpan.FromMinutes(5));
        active.HoldsLease(id, Now + TimeSpan.FromMinutes(3)).Should().BeTrue();

        active.Revoke(id);
        active.HoldsLease(id, Now).Should().BeFalse();
        active.HoldsLease(Guid.NewGuid(), Now).Should().BeTrue(because: "a run not held here is not this process's to fence");
    }

    [Fact]
    public async Task A_heartbeat_confirms_renewed_leases_and_stops_a_run_another_instance_closed()
    {
        var store = new InMemoryWorkflowRunStore();
        var tracker = new InMemoryWorkflowRunTracker();
        var active = new ActiveWorkflowRuns();
        var services = new ServiceCollection().AddSingleton<IWorkflowRunStore>(store).BuildServiceProvider();
        var lease = new WorkflowRunLeaseService(
            services.GetRequiredService<IServiceScopeFactory>(), active, tracker, NullLogger<WorkflowRunLeaseService>.Instance, new FixedTime(Now));

        var live = Run(Now.AddMinutes(-1), Now.AddSeconds(10));
        var closed = Run(Now.AddMinutes(-5), Now.AddMinutes(-3));
        closed.Fail(WorkflowRunLease.InterruptedReason, Now.AddMinutes(-2));
        foreach (var run in new[] { live, closed })
        {
            await store.SaveAsync(run, CancellationToken.None);
        }

        using var closedCancellation = new CancellationTokenSource();
        tracker.MarkRunning(closed.Id, closedCancellation);
        using var heldLive = active.Hold(live.Id);
        using var heldClosed = active.Hold(closed.Id);
        active.Confirm([live.Id, closed.Id], Now.AddSeconds(10));

        await lease.TickAsync(CancellationToken.None);

        active.HoldsLease(live.Id, Now + WorkflowRunLease.Duration - WorkflowRunLease.FenceMargin - TimeSpan.FromSeconds(1))
            .Should().BeTrue(because: "the renewal was confirmed");
        active.HoldsLease(closed.Id, Now).Should().BeFalse();
        closedCancellation.IsCancellationRequested.Should().BeTrue();
        closed.Status.Should().Be(WorkflowRunStatus.Failed, because: "the outcome recorded elsewhere stands");
    }

    [Fact]
    public async Task When_the_heartbeat_cannot_renew_the_run_is_fenced_before_its_lease_lapses()
    {
        var store = new Mock<IWorkflowRunStore>();
        store.Setup(s => s.RenewLeasesAsync(It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException());
        var active = new ActiveWorkflowRuns();
        var services = new ServiceCollection().AddSingleton(store.Object).BuildServiceProvider();
        var lease = new WorkflowRunLeaseService(
            services.GetRequiredService<IServiceScopeFactory>(), active, new InMemoryWorkflowRunTracker(),
            NullLogger<WorkflowRunLeaseService>.Instance, new FixedTime(Now));
        var id = Guid.NewGuid();
        using var held = active.Hold(id);
        active.Confirm([id], Now + WorkflowRunLease.Duration);

        await lease.TickAsync(CancellationToken.None);

        active.HoldsLease(id, Now + TimeSpan.FromSeconds(60)).Should().BeTrue();
        active.HoldsLease(id, Now + WorkflowRunLease.Duration - WorkflowRunLease.FenceMargin).Should().BeFalse(
            because: "nothing confirmed a renewal, so the process stops before others may close the run");
    }

    [Fact]
    public async Task Only_an_abandoned_run_is_cancelled_outright()
    {
        var store = new InMemoryWorkflowRunStore();
        var live = Run(Now.AddHours(-1), Now.AddMinutes(1));
        var dead = Run(Now.AddHours(-1), Now.AddMinutes(-1));
        await store.SaveAsync(live, CancellationToken.None);
        await store.SaveAsync(dead, CancellationToken.None);

        (await store.CancelAbandonedAsync(live.Id, Now, Now - WorkflowRunLease.LegacyGrace, "x", CancellationToken.None)).Should().BeFalse();
        (await store.CancelAbandonedAsync(dead.Id, Now, Now - WorkflowRunLease.LegacyGrace, "x", CancellationToken.None)).Should().BeTrue();

        live.Status.Should().Be(WorkflowRunStatus.Running);
        dead.Status.Should().Be(WorkflowRunStatus.Cancelled);
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
