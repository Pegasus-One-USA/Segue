using FHIRBridge.Domain.Entities;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FHIRBridge.UnitTests.Infrastructure;

// Uses the EF in-memory provider with a shared root so a fresh context (a new "request"/scope) reads back what a
// previous context wrote — same pattern as WorkflowSqlStoreTests. Optimistic-concurrency (RowVersion) behavior is
// not exercised here: EF Core's in-memory provider doesn't reproduce SQL Server's real `rowversion` semantics, so a
// DbUpdateConcurrencyException test against it would not prove anything about production behavior.
public sealed class EfBulkExportJobRepositoryTests
{
    private readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;

        return new FHIRBridgeDbContext(options);
    }

    private static BulkExportJob CreateJob(string status = BulkExportJobStatus.Polling, DateTime? nextPollNotBeforeUtc = null)
    {
        var job = new BulkExportJob(
            Guid.NewGuid(),
            BulkExportJobSourcePath.WorkflowNode,
            Guid.NewGuid(),
            sourceConfigurationId: null,
            exportRequestJson: "{}",
            kickedOffOnUtc: DateTime.UtcNow);
        job.MarkKickedOff("https://fhir.example.com/status/1");
        // No AuditingSaveChangesInterceptor is registered against this bare test DbContext (that's an Api/Worker
        // host-level registration) — stamp provenance directly, same as the interceptor would in production.
        job.MarkCreated("test");

        if (status == BulkExportJobStatus.Completed)
        {
            job.MarkCompleted(DateTime.UtcNow);
        }
        else if (nextPollNotBeforeUtc is { } notBefore)
        {
            job.RecordPollAttempt(notBefore);
        }

        return job;
    }

    [Fact]
    public async Task AddAsync_then_GetAsync_round_trips_the_job()
    {
        var job = CreateJob();

        await using (var context = CreateContext())
        {
            await new EfBulkExportJobRepository(context).AddAsync(job, CancellationToken.None);
        }

        await using var assertContext = CreateContext();
        var persisted = await new EfBulkExportJobRepository(assertContext).GetAsync(job.Id, CancellationToken.None);

        persisted.Should().NotBeNull();
        persisted!.Status.Should().Be(BulkExportJobStatus.Polling);
        persisted.StatusUrl.Should().Be("https://fhir.example.com/status/1");
        persisted.SourcePath.Should().Be(BulkExportJobSourcePath.WorkflowNode);
    }

    [Fact]
    public async Task GetPollableAsync_returns_only_polling_jobs_that_are_due()
    {
        var now = DateTime.UtcNow;
        var due = CreateJob(nextPollNotBeforeUtc: now.AddMinutes(-1));
        var notYetDue = CreateJob(nextPollNotBeforeUtc: now.AddMinutes(5));
        var neverPolled = CreateJob();
        var completed = CreateJob(status: BulkExportJobStatus.Completed);

        await using (var context = CreateContext())
        {
            var repository = new EfBulkExportJobRepository(context);
            await repository.AddAsync(due, CancellationToken.None);
            await repository.AddAsync(notYetDue, CancellationToken.None);
            await repository.AddAsync(neverPolled, CancellationToken.None);
            await repository.AddAsync(completed, CancellationToken.None);
        }

        await using var assertContext = CreateContext();
        var pollable = await new EfBulkExportJobRepository(assertContext).GetPollableAsync(now, maxBatchSize: 50, CancellationToken.None);

        pollable.Select(job => job.Id).Should().BeEquivalentTo([due.Id, neverPolled.Id]);
    }

    [Fact]
    public async Task UpdateAsync_persists_mutations_made_on_a_tracked_instance()
    {
        var job = CreateJob();

        await using var context = CreateContext();
        var repository = new EfBulkExportJobRepository(context);
        await repository.AddAsync(job, CancellationToken.None);

        var tracked = await repository.GetAsync(job.Id, CancellationToken.None);
        tracked!.MarkCompleted(DateTime.UtcNow);
        await repository.UpdateAsync(tracked, CancellationToken.None);

        await using var assertContext = CreateContext();
        var persisted = await new EfBulkExportJobRepository(assertContext).GetAsync(job.Id, CancellationToken.None);
        persisted!.Status.Should().Be(BulkExportJobStatus.Completed);
    }

    [Fact]
    public async Task GetPendingByWorkflowRunAsync_excludes_the_given_job_and_terminal_siblings()
    {
        var workflowRunId = Guid.NewGuid();
        var pendingSibling = CreateJob();
        var completedSibling = CreateJob(status: BulkExportJobStatus.Completed);
        var self = CreateJob();

        await using (var context = CreateContext())
        {
            var repository = new EfBulkExportJobRepository(context);

            // WorkflowRunId is only set via the constructor's optional parameter — add it through a fresh instance
            // per job so each carries the same run id as the others.
            foreach (var job in new[] { pendingSibling, completedSibling, self })
            {
                var withRun = new BulkExportJob(
                    job.Id,
                    BulkExportJobSourcePath.WorkflowNode,
                    Guid.NewGuid(),
                    sourceConfigurationId: null,
                    exportRequestJson: "{}",
                    kickedOffOnUtc: DateTime.UtcNow,
                    workflowRunId: workflowRunId,
                    workflowNodeId: Guid.NewGuid());
                withRun.MarkKickedOff("https://fhir.example.com/status/1");
                withRun.MarkCreated("test");
                if (job.Status == BulkExportJobStatus.Completed)
                {
                    withRun.MarkCompleted(DateTime.UtcNow);
                }

                await repository.AddAsync(withRun, CancellationToken.None);
            }
        }

        await using var assertContext = CreateContext();
        var pending = await new EfBulkExportJobRepository(assertContext)
            .GetPendingByWorkflowRunAsync(workflowRunId, self.Id, CancellationToken.None);

        pending.Select(job => job.Id).Should().BeEquivalentTo([pendingSibling.Id]);
    }
}
