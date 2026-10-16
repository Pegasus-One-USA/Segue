using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.Runtime.UnitTests.Workflows;

// A failed or cancelled bulk export job is never polled again, so nothing resumed the run paused on it: it stayed
// AwaitingBulkExport ("Running") for ever, on Execution History and in the Bulk Data Status popup, with no event.
public sealed class PausedBulkExportRunCloserTests
{
    [Fact]
    public async Task FailAsync_saves_the_paused_run_as_Failed_then_announces_it()
    {
        var (closer, runStore, notifier, pausedRun) = await CreatePausedRunAsync();

        var closed = await closer.FailAsync(pausedRun.Id, "Bulk export failed: 500 Internal Server Error", CancellationToken.None);

        closed.Should().BeTrue();
        runStore.LastSavedStatus(pausedRun.Id).Should().Be(WorkflowRunStatus.Failed);
        var saved = await runStore.GetAsync(pausedRun.Id, CancellationToken.None);
        saved!.ErrorMessage.Should().Be("Bulk export failed: 500 Internal Server Error");
        saved.CompletedAt.Should().NotBeNull();
        notifier.Events.Should().ContainSingle();
        notifier.Events[0].Event.Should().Match<RunStatusChangedEvent>(e =>
            e.WorkflowRunId == pausedRun.Id
            && e.WorkflowDefinitionId == pausedRun.WorkflowDefinitionId
            && e.Status == "Failed"
            && e.ErrorMessage == "Bulk export failed: 500 Internal Server Error");
        notifier.Events[0].SavedStatusAtNotifyTime.Should().Be(WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task CancelAsync_saves_the_paused_run_as_Cancelled_then_announces_it()
    {
        var (closer, runStore, notifier, pausedRun) = await CreatePausedRunAsync();

        var closed = await closer.CancelAsync(pausedRun.Id, "Cancelled: export cancelled.", CancellationToken.None);

        closed.Should().BeTrue();
        runStore.LastSavedStatus(pausedRun.Id).Should().Be(WorkflowRunStatus.Cancelled);
        notifier.Events.Should().ContainSingle().Which.Event.Status.Should().Be("Cancelled");
        notifier.Events[0].SavedStatusAtNotifyTime.Should().Be(WorkflowRunStatus.Cancelled);
    }

    [Theory]
    [InlineData(WorkflowRunStatus.Succeeded)]
    [InlineData(WorkflowRunStatus.PartialSuccess)]
    [InlineData(WorkflowRunStatus.Failed)]
    [InlineData(WorkflowRunStatus.Running)]
    public async Task A_run_that_is_not_paused_on_a_bulk_export_is_never_overwritten_or_announced(WorkflowRunStatus status)
    {
        var runStore = new SaveRecordingRunStore();
        var notifier = new RecordingNotifier(runStore);
        var closer = new PausedBulkExportRunCloser(runStore, NullLogger<PausedBulkExportRunCloser>.Instance, notifier);
        var run = new WorkflowRun(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        switch (status)
        {
            case WorkflowRunStatus.Succeeded: run.Succeed(DateTimeOffset.UtcNow); break;
            case WorkflowRunStatus.PartialSuccess: run.PartialSucceed("skipped", DateTimeOffset.UtcNow); break;
            case WorkflowRunStatus.Failed: run.Fail("earlier", DateTimeOffset.UtcNow); break;
        }
        await runStore.SaveAsync(run, CancellationToken.None);

        var closed = await closer.FailAsync(run.Id, "late failure", CancellationToken.None);

        closed.Should().BeFalse();
        runStore.LastSavedStatus(run.Id).Should().Be(status);
        notifier.Events.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_twice_announces_once()
    {
        var (closer, _, notifier, pausedRun) = await CreatePausedRunAsync();

        (await closer.FailAsync(pausedRun.Id, "first", CancellationToken.None)).Should().BeTrue();
        (await closer.FailAsync(pausedRun.Id, "second", CancellationToken.None)).Should().BeFalse();

        notifier.Events.Should().ContainSingle();
    }

    [Fact]
    public async Task A_failed_announcement_leaves_the_saved_outcome_alone()
    {
        var runStore = new SaveRecordingRunStore();
        var closer = new PausedBulkExportRunCloser(runStore, NullLogger<PausedBulkExportRunCloser>.Instance, new ThrowingNotifier());
        var pausedRun = await SeedPausedRunAsync(runStore);

        var closed = await closer.FailAsync(pausedRun.Id, "Bulk export failed: timeout", CancellationToken.None);

        closed.Should().BeTrue();
        runStore.LastSavedStatus(pausedRun.Id).Should().Be(WorkflowRunStatus.Failed);
    }

    [Fact]
    public async Task A_failed_save_propagates_and_announces_nothing()
    {
        var runStore = new SaveRecordingRunStore();
        var pausedRun = await SeedPausedRunAsync(runStore);
        runStore.FailSaves = true;
        var notifier = new RecordingNotifier(runStore);
        var closer = new PausedBulkExportRunCloser(runStore, NullLogger<PausedBulkExportRunCloser>.Instance, notifier);

        var close = () => closer.FailAsync(pausedRun.Id, "Bulk export failed", CancellationToken.None);

        await close.Should().ThrowAsync<InvalidOperationException>();
        notifier.Events.Should().BeEmpty();
    }

    private static async Task<(PausedBulkExportRunCloser Closer, SaveRecordingRunStore RunStore, RecordingNotifier Notifier, WorkflowRun PausedRun)>
        CreatePausedRunAsync()
    {
        var runStore = new SaveRecordingRunStore();
        var notifier = new RecordingNotifier(runStore);
        var closer = new PausedBulkExportRunCloser(runStore, NullLogger<PausedBulkExportRunCloser>.Instance, notifier);
        return (closer, runStore, notifier, await SeedPausedRunAsync(runStore));
    }

    private static async Task<WorkflowRun> SeedPausedRunAsync(SaveRecordingRunStore runStore)
    {
        var run = new WorkflowRun(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, correlationId: "closer-test");
        run.AwaitBulkExport("job-1");
        await runStore.SaveAsync(run, CancellationToken.None);
        return run;
    }

    private sealed class RecordingNotifier : IRunStatusNotifier
    {
        private readonly SaveRecordingRunStore _runStore;

        public RecordingNotifier(SaveRecordingRunStore runStore)
        {
            _runStore = runStore;
        }

        public List<(RunStatusChangedEvent Event, WorkflowRunStatus? SavedStatusAtNotifyTime)> Events { get; } = [];

        public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
        {
            Events.Add((statusEvent, _runStore.LastSavedStatus(statusEvent.WorkflowRunId)));
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingNotifier : IRunStatusNotifier
    {
        public Task NotifyAsync(RunStatusChangedEvent statusEvent, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Redis is unreachable.");
    }
}
