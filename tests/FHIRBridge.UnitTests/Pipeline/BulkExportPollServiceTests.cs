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

    /// <summary>Uses up attempts on a job, as earlier ticks' status polls would have.</summary>
    private static void UseAttempts(BulkExportJob job, int count)
    {
        for (var i = 0; i < count; i++)
        {
            job.RecordPollAttempt(DateTime.UtcNow);
        }
    }

    private static BulkExportJob CreateJob(
        Guid sourceConnectionId, string sourcePath = BulkExportJobSourcePath.WorkflowNode, string? requestedResourceTypesJson = null)
    {
        var job = new BulkExportJob(
            Guid.NewGuid(), sourcePath, sourceConnectionId, sourceConfigurationId: null,
            exportRequestJson: "{}", kickedOffOnUtc: DateTime.UtcNow,
            workflowRunId: sourcePath == BulkExportJobSourcePath.WorkflowNode ? Guid.NewGuid() : null,
            workflowNodeId: sourcePath == BulkExportJobSourcePath.WorkflowNode ? Guid.NewGuid() : null,
            requestedResourceTypesJson: requestedResourceTypesJson);
        job.MarkKickedOff("https://fhir.example.com/status/1");
        return job;
    }

    /// <summary>Closes the workflow run paused on a job that failed — see BulkExportPollService.FailJobAsync.</summary>
    private readonly Mock<IPausedBulkExportRunCloser> _runCloser = new();

    private (Mock<IBulkExportJobRepository> Repository, Mock<IFhirBulkExportClient> Client,
        Mock<ISourceConnectionRuntimeResolver> Resolver, Mock<IRankedWorkflowOrchestrator> Orchestrator, BulkExportPollService Service)
        CreateSut()
    {
        var repository = new Mock<IBulkExportJobRepository>();
        var client = new Mock<IFhirBulkExportClient>();
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        var orchestrator = new Mock<IRankedWorkflowOrchestrator>();
        var service = new BulkExportPollService(
            repository.Object, client.Object, resolver.Object, orchestrator.Object, _runCloser.Object, NullLogger<BulkExportPollService>.Instance);

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
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task InProgress_result_marks_the_job_failed_once_the_attempt_cap_is_reached()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.InProgress));

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 1, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        _runCloser.Verify(c => c.FailAsync(
            job.WorkflowRunId!.Value, "Bulk export failed: Bulk export did not complete after 1 status polls.", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Failed_result_marks_the_job_failed_with_the_returned_message()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Failed, ErrorMessage: "500 Internal Server Error"));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().Be("500 Internal Server Error");
        _runCloser.Verify(c => c.FailAsync(
            job.WorkflowRunId!.Value, "Bulk export failed: 500 Internal Server Error", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task A_failed_job_closes_its_run_before_the_job_is_saved_as_Failed()
    {
        // PR #247 review: saving the job Failed first meant a closer failure (a transient store error) left a terminal
        // job — never polled again — and a run AwaitingBulkExport for ever. The run is closed first now; the job is
        // saved Failed only once that has worked.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync((FhirSourceConfiguration?)null);
        var order = new List<string>();
        repository.Setup(r => r.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"job {job.Status}"))
            .Returns(Task.CompletedTask);
        _runCloser.Setup(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => order.Add($"run closed (job {job.Status})"))
            .ReturnsAsync(true);

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        order.Should().Equal($"run closed (job {BulkExportJobStatus.Polling})", $"job {BulkExportJobStatus.Failed}");
    }

    [Fact]
    public async Task A_run_that_cannot_be_closed_leaves_the_job_polling_so_the_next_tick_retries()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Failed, ErrorMessage: "500 Internal Server Error"));
        _runCloser.SetupSequence(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The run store is unavailable."))
            .ReturnsAsync(true);

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Polling); // not terminal, so it is polled again
        job.PollAttemptCount.Should().Be(1);

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().Be("500 Internal Server Error");
        _runCloser.Verify(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task A_resume_failure_is_reported_as_itself_even_when_closing_the_run_also_fails()
    {
        // PR #247 review: the closer's own exception replaced the resume's, so the wrong error was recorded.
        var job = CreateJob(Guid.NewGuid());
        var repository = new Mock<IBulkExportJobRepository>();
        var client = new Mock<IFhirBulkExportClient>();
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        var orchestrator = new Mock<IRankedWorkflowOrchestrator>();
        var exceptionManager = new Mock<FHIRBridge.Governance.IGlobalExceptionManager>();
        var captured = new List<Exception>();
        exceptionManager
            .Setup(m => m.CaptureAsync(It.IsAny<Exception>(), It.IsAny<FHIRBridge.Governance.ExceptionContext>(), It.IsAny<CancellationToken>()))
            .Callback<Exception, FHIRBridge.Governance.ExceptionContext, CancellationToken>((exception, _, _) => captured.Add(exception))
            .ReturnsAsync(new FHIRBridge.Governance.ErrorReport("ERR-TEST-000001", FHIRBridge.Governance.ErrorCategory.Unknown, "x", null));
        var service = new BulkExportPollService(
            repository.Object, client.Object, resolver.Object, orchestrator.Object, _runCloser.Object,
            NullLogger<BulkExportPollService>.Instance, exceptionManager: exceptionManager.Object);
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        orchestrator.Setup(o => o.ResumeAfterBulkExportAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Workflow definition was not found."));
        _runCloser.Setup(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The run store is unavailable."));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        captured.Should().ContainSingle().Which.Message.Should().Be("Workflow definition was not found.");
        job.Status.Should().Be(BulkExportJobStatus.Completed);
    }

    // A poll that THROWS (token request refused — revoked/expired credentials, a disabled client — or the network/VPN
    // down) used to leave the job untouched: no attempt counted, polled again every tick for ever, and its run shown
    // Running for ever. It now counts as an attempt, so the attempt cap ends it like any other export that never
    // completes, and the run is closed.
    [Theory]
    [InlineData(RuntimeSourceType.Epic)]
    [InlineData(RuntimeSourceType.Healow)]
    [InlineData(RuntimeSourceType.Athenahealth)]
    [InlineData(RuntimeSourceType.GenericFhir)]
    public async Task A_poll_that_throws_counts_as_an_attempt_and_the_cap_closes_the_run(RuntimeSourceType vendor)
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        var source = Source with { SourceType = vendor };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Token request failed: invalid_client"));

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 2, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Polling);
        job.PollAttemptCount.Should().Be(1);
        job.NextPollNotBeforeUtc.Should().BeAfter(DateTime.UtcNow);
        repository.Verify(r => r.UpdateAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        _runCloser.Verify(c => c.FailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 2, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().Contain("could not be read after 2 attempts").And.Contain("invalid_client");
        _runCloser.Verify(c => c.FailAsync(
            job.WorkflowRunId!.Value,
            It.Is<string>(reason => reason.Contains("invalid_client")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task A_download_that_throws_counts_as_an_attempt()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("file link expired"));
        UseAttempts(job, BulkExportPollService.DownloadAttemptBudget); // the last download attempt the budget allows

        await service.PollDueJobsAsync(50, 5, maxPollAttempts: 1, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        _runCloser.Verify(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        orchestrator.Verify(o => o.ResumeAfterBulkExportAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task A_resume_that_throws_closes_the_still_paused_run_and_leaves_the_job_completed()
    {
        // The resume failed before the run restarted (here: its workflow was deleted), so nothing would ever close the
        // run. The job itself did complete and must stay Completed, not count a poll attempt. The closer only acts on
        // a run still AwaitingBulkExport, so a run the orchestrator already closed itself is left as it is.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        orchestrator.Setup(o => o.ResumeAfterBulkExportAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Workflow definition was not found."));

        await service.PollDueJobsAsync(50, 5, maxPollAttempts: 1, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Completed);
        job.PollAttemptCount.Should().Be(0);
        _runCloser.Verify(c => c.FailAsync(
            job.WorkflowRunId!.Value,
            It.Is<string>(reason => reason.Contains("Workflow definition was not found.")),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Downloads_keep_their_own_retry_budget_after_a_long_status_phase()
    {
        // PR #247 third review: downloads shared the 120-attempt cap with status polls, so an export that needed 119
        // status polls failed on its FIRST download error although the source had finished it. A completed export now
        // gets DownloadAttemptBudget more attempts on top of the cap, however many status polls came before.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("The file server is briefly unavailable."));
        UseAttempts(job, 119);

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Polling); // retried, not failed
        job.PollAttemptCount.Should().Be(120);
        _runCloser.Verify(c => c.FailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        UseAttempts(job, BulkExportPollService.DownloadAttemptBudget - 1); // the rest of the download budget
        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().StartWith("The bulk export completed, but its results could not be downloaded");
    }

    [Fact]
    public async Task A_status_read_failure_during_download_retries_keeps_the_download_budget()
    {
        // PR #247 fourth review: each download retry reads the status again first. When that read threw (an outage or a
        // refused token — the likeliest reasons the download failed too) the higher cap was not applied, and a count
        // already past the ordinary cap failed the job at once as "status could not be read", although the source had
        // finished the export. A job still polling with more attempts than the ordinary cap got there only through
        // download retries, so it keeps the download budget, delay and wording.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("No route to host."));
        UseAttempts(job, 125); // 120 status polls, then 5 failed download retries

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Polling);
        job.PollAttemptCount.Should().Be(126);
        job.NextPollNotBeforeUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(4)); // the download retry delay
        _runCloser.Verify(c => c.FailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        UseAttempts(job, 120 + BulkExportPollService.DownloadAttemptBudget - 1 - job.PollAttemptCount);
        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().StartWith("The bulk export completed, but its results could not be downloaded");
    }

    [Fact]
    public async Task A_status_read_failure_still_stops_at_the_ordinary_cap()
    {
        // The download budget applies only once the source reported the export complete.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("No route to host."));
        UseAttempts(job, 119);

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        job.ErrorMessage.Should().StartWith("Bulk export status could not be read after 120 attempts.");
    }

    // PR #247 third review: a failed download's message carries the file URL — for a source whose file URLs are
    // pre-signed (requiresAccessToken: false), the URL itself grants access to the bulk NDJSON — and up to 300
    // characters of the response body. Once a job failed, that text became the job's error, the run's error and its
    // Errors entry, the popup's reason, and the run-status event broadcast to every connected browser. Only the host
    // of any URL may leave the poller, and no markup.
    [Fact]
    public async Task A_failed_download_never_puts_a_file_url_or_response_markup_in_the_job_or_run_error()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://files.example.com/exports/7f3a/Patient.ndjson?sv=2024&sig=SECRET") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException(
                "Bulk export file download returned 403 (Forbidden) for https://files.example.com/exports/7f3a/Patient.ndjson?sv=2024&sig=SECRET. "
                + "<?xml version=\"1.0\"?><Error><Code>AuthenticationFailed</Code><Url>https://files.example.com/exports/7f3a/Patient.ndjson?sig=SECRET</Url></Error>"));
        string? runReason = null;
        _runCloser.Setup(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, reason, _) => runReason = reason)
            .ReturnsAsync(true);
        UseAttempts(job, BulkExportPollService.DownloadAttemptBudget);

        await service.PollDueJobsAsync(50, 5, maxPollAttempts: 1, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        foreach (var text in new[] { job.ErrorMessage!, runReason! })
        {
            text.Should().NotContain("SECRET").And.NotContain("sig=").And.NotContain("/exports/7f3a")
                .And.NotContain("<").And.NotContain(">");
            text.Should().Contain("403 (Forbidden)").And.Contain("https://files.example.com");
        }
    }

    [Fact]
    public async Task A_failed_status_poll_never_puts_a_url_query_or_an_html_page_in_the_job_or_run_error()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(
                BulkExportPollStatus.Failed,
                ErrorMessage: "Bulk export status poll returned 500 (Internal Server Error). <html>\n<body>Retry at https://fhir.example.com/status/1?_token=SECRET</body></html>"));
        string? runReason = null;
        _runCloser.Setup(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, string, CancellationToken>((_, reason, _) => runReason = reason)
            .ReturnsAsync(true);

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        foreach (var text in new[] { job.ErrorMessage!, runReason! })
        {
            text.Should().NotContain("SECRET").And.NotContain("_token").And.NotContain("<").And.NotContain("\n");
            text.Should().Contain("500 (Internal Server Error)").And.Contain("https://fhir.example.com");
        }
    }

    [Fact]
    public async Task A_failed_job_that_belongs_to_no_workflow_run_closes_nothing()
    {
        var job = CreateJob(Guid.NewGuid(), sourcePath: BulkExportJobSourcePath.ConfiguredPipeline);
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Failed, ErrorMessage: "boom"));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        _runCloser.Verify(c => c.FailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
                job.WorkflowRunId!.Value, job.WorkflowNodeId!.Value, job.PriorNodeOutputsJson, job.ContextJson, resources,
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
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
    public async Task Completed_result_with_manifest_errors_downloads_and_forwards_the_partial_failures()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        var errorFiles = new[] { new BulkExportFile("OperationOutcome", "https://fhir.example.com/files/error.ndjson") };
        var resources = new List<ResourceEnvelope> { new("Patient", "p1", "{}", null, null) };
        var partialFailures = new[]
        {
            new BulkExportPartialFailure("error", "not-supported", "Resource type 'MedicationAdministration' is not supported for this client."),
        };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files, ErrorFiles: errorFiles));
        client.Setup(c => c.DownloadResultsAsync(files, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);
        client.Setup(c => c.DownloadPartialFailuresAsync(errorFiles, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(partialFailures);

        IReadOnlyList<string>? capturedReasons = null;
        orchestrator
            .Setup(o => o.ResumeAfterBulkExportAsync(
                job.WorkflowRunId!.Value, job.WorkflowNodeId!.Value, job.PriorNodeOutputsJson, job.ContextJson, resources,
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string?, string?, IReadOnlyList<ResourceEnvelope>, IReadOnlyList<string>?, CancellationToken>(
                (_, _, _, _, _, reasons, _) => capturedReasons = reasons)
            .ReturnsAsync(new WorkflowRunResult(
                new WorkflowRun(job.WorkflowRunId!.Value, Guid.NewGuid(), DateTimeOffset.UtcNow), new Dictionary<Guid, WorkflowNodeOutput>()));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Completed);
        capturedReasons.Should().ContainSingle()
            .Which.Should().Contain("MedicationAdministration").And.Contain("not-supported");
    }

    [Fact]
    public async Task Completed_result_filters_manifest_errors_down_to_the_nodes_actually_requested_resource_types()
    {
        // Reproduces the Epic Group-export scenario: the node only requested "Patient", but ResolveTypeParameter
        // omits _type entirely for a lone-Patient Group export (dodging a different Epic bug), so the server
        // attempts every resource type it supports and reports each unrequested/unauthorized one as a manifest
        // error. None of that noise should reach the caller — only reasons naming a type this node actually asked for.
        var job = CreateJob(Guid.NewGuid(), requestedResourceTypesJson: "[\"Patient\"]");
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        var errorFiles = new[] { new BulkExportFile("OperationOutcome", "https://fhir.example.com/files/error.ndjson") };
        var resources = new List<ResourceEnvelope> { new("Patient", "p1", "{}", null, null) };
        var partialFailures = new[]
        {
            new BulkExportPartialFailure("information", "invalid", "Unknown parameter: _OUTPUTFORMAT. Parameter has been ignored."),
            new BulkExportPartialFailure("information", "suppressed", "The following resources are not authorized: claim, imagingstudy."),
            new BulkExportPartialFailure("information", "suppressed", "Observation"),
            new BulkExportPartialFailure("information", "suppressed", "Patient export limited by group membership."),
        };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files, ErrorFiles: errorFiles));
        client.Setup(c => c.DownloadResultsAsync(files, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(resources);
        client.Setup(c => c.DownloadPartialFailuresAsync(errorFiles, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(partialFailures);

        IReadOnlyList<string>? capturedReasons = null;
        orchestrator
            .Setup(o => o.ResumeAfterBulkExportAsync(
                job.WorkflowRunId!.Value, job.WorkflowNodeId!.Value, job.PriorNodeOutputsJson, job.ContextJson, resources,
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, Guid, string?, string?, IReadOnlyList<ResourceEnvelope>, IReadOnlyList<string>?, CancellationToken>(
                (_, _, _, _, _, reasons, _) => capturedReasons = reasons)
            .ReturnsAsync(new WorkflowRunResult(
                new WorkflowRun(job.WorkflowRunId!.Value, Guid.NewGuid(), DateTimeOffset.UtcNow), new Dictionary<Guid, WorkflowNodeOutput>()));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        capturedReasons.Should().ContainSingle()
            .Which.Should().Contain("Patient export limited by group membership");
    }

    [Fact]
    public async Task Completed_result_downloads_only_the_files_for_requested_resource_types()
    {
        // The node requested Patient + Condition, but the server's manifest also lists Binary and Medication
        // (resources it considers related to the requested _type — the eCW referenced-resource behavior). Only the
        // requested-type files should be downloaded; the unrequested ones are dropped before any download happens.
        var job = CreateJob(Guid.NewGuid(), requestedResourceTypesJson: "[\"Patient\",\"Condition\"]");
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var patientFile = new BulkExportFile("Patient", "https://fhir.example.com/files/patient.ndjson");
        var conditionFile = new BulkExportFile("Condition", "https://fhir.example.com/files/condition.ndjson");
        var binaryFile = new BulkExportFile("Binary", "https://fhir.example.com/files/binary.ndjson");
        var medicationFile = new BulkExportFile("Medication", "https://fhir.example.com/files/medication.ndjson");
        var manifest = new[] { patientFile, conditionFile, binaryFile, medicationFile };

        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: manifest));

        IReadOnlyList<BulkExportFile>? downloadedFiles = null;
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BulkExportFile>, FhirSourceConfiguration, CancellationToken>((files, _, _) => downloadedFiles = files)
            .ReturnsAsync([]);
        orchestrator.Setup(o => o.ResumeAfterBulkExportAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunResult(
                new WorkflowRun(job.WorkflowRunId!.Value, Guid.NewGuid(), DateTimeOffset.UtcNow), new Dictionary<Guid, WorkflowNodeOutput>()));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        downloadedFiles.Should().NotBeNull();
        downloadedFiles!.Select(f => f.ResourceType).Should().BeEquivalentTo(["Patient", "Condition"]);
    }

    [Theory]
    // A write-back that asked for notes keeps Binary: it holds the note text, and a bulk-only app's token is
    // refused on a plain Binary/{id} read.
    [InlineData(true, "[\"Patient\",\"DocumentReference\"]", true)]
    // Without an EHR write-back node, or without notes requested, the strict filter holds.
    [InlineData(false, "[\"Patient\",\"DocumentReference\"]", false)]
    [InlineData(true, "[\"Patient\",\"Condition\"]", false)]
    public async Task Completed_result_keeps_binary_only_for_an_ehr_write_back_that_wants_notes(
        bool hasWriteBack, string requested, bool binaryKept)
    {
        var job = CreateJob(Guid.NewGuid(), requestedResourceTypesJson: requested);
        var repository = new Mock<IBulkExportJobRepository>();
        var client = new Mock<IFhirBulkExportClient>();
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        var orchestrator = new Mock<IRankedWorkflowOrchestrator>();

        var workflow = new WorkflowDefinition(Guid.NewGuid(), "ecw-to-epic", 1);
        workflow.AddNode(FHIRBridge.Runtime.Application.Workflows.Catalog.WorkflowNodeTypes.EClinicalWorksSource, WorkflowNodeCategory.Source, 0);
        workflow.AddNode(
            hasWriteBack
                ? FHIRBridge.Runtime.Application.Workflows.Catalog.WorkflowNodeTypes.EhrWriteBackDestination
                : FHIRBridge.Runtime.Application.Workflows.Catalog.WorkflowNodeTypes.FhirRepositoryDestination,
            WorkflowNodeCategory.Destination, 1);
        var runStore = new Mock<FHIRBridge.Runtime.Application.Workflows.Storage.IWorkflowRunStore>();
        runStore.Setup(s => s.GetAsync(job.WorkflowRunId!.Value, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRun(job.WorkflowRunId!.Value, workflow.Id, DateTimeOffset.UtcNow));
        var definitionStore = new Mock<FHIRBridge.Runtime.Application.Workflows.Storage.IWorkflowDefinitionStore>();
        definitionStore.Setup(s => s.GetAsync(workflow.Id, It.IsAny<CancellationToken>())).ReturnsAsync(workflow);

        var service = new BulkExportPollService(
            repository.Object, client.Object, resolver.Object, orchestrator.Object, _runCloser.Object, NullLogger<BulkExportPollService>.Instance,
            runStore: runStore.Object, definitionStore: definitionStore.Object);

        var manifest = new[]
        {
            new BulkExportFile("Patient", "https://fhir.example.com/files/patient.ndjson"),
            new BulkExportFile("DocumentReference", "https://fhir.example.com/files/doc.ndjson"),
            new BulkExportFile("Condition", "https://fhir.example.com/files/condition.ndjson"),
            new BulkExportFile("Binary", "https://fhir.example.com/files/binary.ndjson"),
        };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: manifest));
        IReadOnlyList<BulkExportFile>? downloadedFiles = null;
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BulkExportFile>, FhirSourceConfiguration, CancellationToken>((files, _, _) => downloadedFiles = files)
            .ReturnsAsync([]);
        orchestrator.Setup(o => o.ResumeAfterBulkExportAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunResult(
                new WorkflowRun(job.WorkflowRunId!.Value, workflow.Id, DateTimeOffset.UtcNow), new Dictionary<Guid, WorkflowNodeOutput>()));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        downloadedFiles!.Select(f => f.ResourceType).Contains("Binary").Should().Be(binaryKept);
    }

    [Fact]
    public async Task Completed_result_downloads_all_files_when_none_match_the_requested_types()
    {
        // Guard: a server that labels every output file generically ("Resource") would match nothing — filtering must
        // fall back to downloading everything rather than silently turning a real export into an empty one.
        var job = CreateJob(Guid.NewGuid(), requestedResourceTypesJson: "[\"Patient\",\"Condition\"]");
        var (repository, client, resolver, orchestrator, service) = CreateSut();
        var manifest = new[]
        {
            new BulkExportFile("Resource", "https://fhir.example.com/files/1.ndjson"),
            new BulkExportFile("Resource", "https://fhir.example.com/files/2.ndjson"),
        };

        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: manifest));

        IReadOnlyList<BulkExportFile>? downloadedFiles = null;
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BulkExportFile>, FhirSourceConfiguration, CancellationToken>((files, _, _) => downloadedFiles = files)
            .ReturnsAsync([]);
        orchestrator.Setup(o => o.ResumeAfterBulkExportAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowRunResult(
                new WorkflowRun(job.WorkflowRunId!.Value, Guid.NewGuid(), DateTimeOffset.UtcNow), new Dictionary<Guid, WorkflowNodeOutput>()));

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        downloadedFiles.Should().NotBeNull();
        downloadedFiles!.Should().HaveCount(2);
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

    [Fact]
    public async Task An_InProgress_poll_whose_save_fails_counts_one_attempt_not_two()
    {
        // PR #247 review: the InProgress branch recorded the attempt, its save threw, and the failure handler recorded
        // it again, so one tick with a transient store error used up two attempts of the cap.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.InProgress, RetryAfter: TimeSpan.FromSeconds(30)));
        repository.SetupSequence(r => r.UpdateAsync(job, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The job store is unavailable."))
            .Returns(Task.CompletedTask);

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 120, CancellationToken.None);

        job.PollAttemptCount.Should().Be(1);
        job.Status.Should().Be(BulkExportJobStatus.Polling);
        // The failure handler only re-saves the attempt the InProgress branch recorded, keeping its RetryAfter.
        job.NextPollNotBeforeUtc.Should().BeAfter(DateTime.UtcNow.AddSeconds(20));
        repository.Verify(r => r.UpdateAsync(job, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task An_InProgress_poll_at_the_cap_whose_close_fails_is_not_counted_past_the_cap()
    {
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.InProgress));
        _runCloser.SetupSequence(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The run store is unavailable."))
            .ReturnsAsync(true);

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 1, CancellationToken.None);

        job.PollAttemptCount.Should().Be(1);
        job.Status.Should().Be(BulkExportJobStatus.Failed); // the failure handler retried the close within the same tick
    }

    [Fact]
    public async Task A_failed_download_of_a_completed_export_is_retried_after_the_download_delay_and_named_as_such()
    {
        // PR #247 review: a download failure was retried on the short status-poll interval, so the attempt cap could
        // fail an export that had succeeded within minutes. It now waits DownloadRetryDelay, and the cap's error says
        // the export completed but its results could not be downloaded.
        var job = CreateJob(Guid.NewGuid());
        var (repository, client, resolver, _, service) = CreateSut();
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TaskCanceledException("The download timed out."));

        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 2, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Polling);
        job.PollAttemptCount.Should().Be(1);
        job.NextPollNotBeforeUtc.Should().BeAfter(DateTime.UtcNow.AddMinutes(4));

        UseAttempts(job, BulkExportPollService.DownloadAttemptBudget); // up to one short of the download cap
        await service.PollDueJobsAsync(50, defaultPollIntervalSeconds: 5, maxPollAttempts: 2, CancellationToken.None);

        job.Status.Should().Be(BulkExportJobStatus.Failed);
        var cap = 2 + BulkExportPollService.DownloadAttemptBudget;
        job.ErrorMessage.Should().StartWith($"The bulk export completed, but its results could not be downloaded after {cap} attempts.")
            .And.Contain("The download timed out.");
    }

    [Fact]
    public async Task A_resume_failure_whose_run_is_no_longer_paused_is_logged_as_not_closed()
    {
        // PR #247 review: a run left Running (its pause was never saved) whose resume then threw before any node ran
        // is closed by nobody, and FailAsync's false return was ignored without a trace.
        var job = CreateJob(Guid.NewGuid());
        var repository = new Mock<IBulkExportJobRepository>();
        var client = new Mock<IFhirBulkExportClient>();
        var resolver = new Mock<ISourceConnectionRuntimeResolver>();
        var orchestrator = new Mock<IRankedWorkflowOrchestrator>();
        var logger = new Mock<Microsoft.Extensions.Logging.ILogger<BulkExportPollService>>();
        var service = new BulkExportPollService(
            repository.Object, client.Object, resolver.Object, orchestrator.Object, _runCloser.Object, logger.Object);
        var files = new[] { new BulkExportFile("Patient", "https://fhir.example.com/files/1.ndjson") };
        repository.Setup(r => r.GetPollableAsync(It.IsAny<DateTime>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([job]);
        resolver.Setup(r => r.ResolveAsync(job.SourceConnectionId, null, null, It.IsAny<CancellationToken>(), null, null))
            .ReturnsAsync(Source);
        client.Setup(c => c.PollOnceAsync(job.StatusUrl!, Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BulkExportPollResult(BulkExportPollStatus.Completed, Files: files));
        client.Setup(c => c.DownloadResultsAsync(It.IsAny<IReadOnlyList<BulkExportFile>>(), Source, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new ResourceEnvelope("Patient", "p1", "{}", null, null)]);
        orchestrator.Setup(o => o.ResumeAfterBulkExportAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
                It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The EHR data dump could not be written."));
        _runCloser.Setup(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await service.PollDueJobsAsync(50, 5, 120, CancellationToken.None);

        logger.Verify(l => l.Log(
            Microsoft.Extensions.Logging.LogLevel.Error,
            It.IsAny<Microsoft.Extensions.Logging.EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains("was not closed after its resume failed")
                && state.ToString()!.Contains(job.WorkflowRunId!.Value.ToString())),
            null,
            It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.Once);
    }
}
