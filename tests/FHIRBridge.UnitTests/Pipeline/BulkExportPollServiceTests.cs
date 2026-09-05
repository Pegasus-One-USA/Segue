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
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ResourceEnvelope>>(),
            It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
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
}
