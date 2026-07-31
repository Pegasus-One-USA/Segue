using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.DTOs;
using FHIRBridge.Runtime.Application.Workflows;
using FHIRBridge.Runtime.Domain.Enums;
using FHIRBridge.Runtime.Domain.ValueObjects;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Pipeline;

public sealed class BulkExportPollServiceTests
{
    private static readonly FhirSourceConfiguration Source = new(
        RuntimeSourceType.Epic, "Epic", "https://fhir.example.com/R4", "https://auth/token", "client-1",
        null, null, []);

    private static BulkExportJob CreateJob(Guid sourceConnectionId, string sourcePath = BulkExportJobSourcePath.WorkflowNode)
    {
        var job = new BulkExportJob(
            Guid.NewGuid(), sourcePath, sourceConnectionId, sourceConfigurationId: null,
            exportRequestJson: "{}", kickedOffOnUtc: DateTime.UtcNow,
            workflowRunId: sourcePath == BulkExportJobSourcePath.WorkflowNode ? Guid.NewGuid() : null,
            workflowNodeId: sourcePath == BulkExportJobSourcePath.WorkflowNode ? Guid.NewGuid() : null);
        job.MarkKickedOff("https://fhir.example.com/status/1");
        return job;
    }

    private static (Mock<IBulkExportJobRepository> Repository, Mock<IFhirBulkExportClient> Client,
        Mock<ISourceConnectionRuntimeResolver> Resolver, Mock<IRankedWorkflowOrchestrator> Orchestrator, BulkExportPollService Service)
        CreateSut()
    {
        var repository = new Mock<IBulkExportJobRepository>();
        var client = new Mock<IFhirBulkExportClient>();
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        var orchestrator = new Mock<IRankedWorkflowOrchestrator>();
        var service = new BulkExportPollService(
            repository.Object, client.Object, resolver.Object, orchestrator.Object, NullLogger<BulkExportPollService>.Instance);

        return (repository, client, resolver, orchestrator, service);
    }

    [Fact]
    public async Task InProgress_result_records_a_poll_attempt_and_does_not_run_any_continuation()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.InProgress, RetryAfter: TimeSpan.FromSeconds(5)));

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Polling);
        job.PollAttemptCount.Should().Be(1);
        repository.Verify(r => r.UpdateAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        orchestrator.Verify(o => o.ResumeAfterBulkExportAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InProgress_result_marks_the_job_failed_once_the_attempt_cap_is_reached()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.InProgress));

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 1, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
    }

    [Fact]
    public async Task Failed_result_marks_the_job_failed_with_the_returned_message()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Failed, ErrorMessage: "500 Internal Server Error"));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().Be("500 Internal Server Error");
    }

    [Fact]
    public async Task Completed_result_downloads_results_marks_completed_before_resuming_and_resumes_the_workflow_run()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        var resources = new List<ResourceEnvelope> { new("Patient", "p1", "{}", null, null) };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(files, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);

        var updateCallOrder = new List<string>();
        repository.Setup(r => r.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .Callback(() => updateCallOrder.Add("update"))
            .Returns(Task.CompletedTask);
        orchestrator
            .Setup(o => o.ResumeAfterBulkExportAsync(
                job.WorkflowRunId!.Value, job.WorkflowNodeId!.Value, job.PriorNodeOutputsJson, job.ContextJson, resources, It.IsAny<CancellationToken>()))
            .Callback(() => updateCallOrder.Add("resume"))
            .ReturnsAsync(new WorkflowRunResult(
                new WorkflowRun(job.WorkflowRunId!.Value, Guid.NewGuid(), DateTimeOffset.UtcNow), new Dictionary<Guid, WorkflowNodeOutput>()));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Completed);
        // Completion must be persisted BEFORE the continuation runs, so a crash mid-continuation doesn't cause a
        // re-poll of an already-finished (and no-longer-servable) $export job.
        updateCallOrder.Should().Equal("update", "resume");
    }

    [Fact]
    public async Task Missing_source_connection_marks_the_job_failed_without_calling_the_bulk_export_client()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync((FhirSourceConfiguration?)null);

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        client.Verify(c => c.PollOnceAsync(It.IsAny<string>(), It.IsAny<FhirSourceConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task One_job_throwing_does_not_stop_the_rest_of_the_batch()
    {
        var throwingJob = CreateJob(Guid.NewGuid());
        var healthyJob = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([throwingJob, healthyJob]);
        resolver.Setup(r => r.ResolveAsync(throwingJob.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ThrowsAsync(new InvalidOperationException("boom"));
        resolver.Setup(r => r.ResolveAsync(healthyJob.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(healthyJob.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.InProgress));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        healthyJob.PollAttemptCount.Should().Be(1);
    }
}
