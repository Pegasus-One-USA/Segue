using FHIRBridge.Api.Controllers.V1;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.Abstractions.Connectors;
using FHIRBridge.Runtime.Application.Abstractions.Sources;
using FHIRBridge.Runtime.Application.Workflows;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Pipeline;

// PR #247 re-review: cancelling a job that has already ended now also closes a run it left paused. That repair is best
// effort — a store error while closing must not turn the endpoint's idempotent 409 into a 500.
public sealed class BulkExportJobsControllerTests
{
    [Theory]
    [InlineData(BulkExportJobStatus.Cancelled)]
    [InlineData(BulkExportJobStatus.Failed)]
    public async Task Cancel_of_an_ended_job_still_answers_409_when_closing_its_run_fails(string endedAs)
    {
        var job = CreateEndedJob(endedAs);
        var (controller, closer) = CreateController(job);
        closer.Setup(c => c.CancelAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The run store is unavailable."));
        closer.Setup(c => c.FailAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The run store is unavailable."));

        var result = await controller.Cancel(job.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
    }

    [Theory]
    [InlineData(BulkExportJobStatus.Cancelled)]
    [InlineData(BulkExportJobStatus.Failed)]
    public async Task Cancel_of_an_ended_job_closes_a_run_it_left_paused_and_answers_409(string endedAs)
    {
        var job = CreateEndedJob(endedAs);
        var (controller, closer) = CreateController(job);

        var result = await controller.Cancel(job.Id, CancellationToken.None);

        result.Should().BeOfType<ConflictObjectResult>();
        if (endedAs == BulkExportJobStatus.Cancelled)
        {
            closer.Verify(c => c.CancelAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        else
        {
            closer.Verify(c => c.FailAsync(job.WorkflowRunId!.Value, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        }
    }

    private static BulkExportJob CreateEndedJob(string endedAs)
    {
        var job = new BulkExportJob(
            Guid.NewGuid(), BulkExportJobSourcePath.WorkflowNode, Guid.NewGuid(), sourceConfigurationId: null,
            exportRequestJson: "{}", kickedOffOnUtc: DateTime.UtcNow, workflowRunId: Guid.NewGuid(), workflowNodeId: Guid.NewGuid());
        job.MarkKickedOff("https://fhir.example.com/status/1");
        if (endedAs == BulkExportJobStatus.Cancelled)
        {
            job.MarkCancelled(DateTime.UtcNow);
        }
        else
        {
            job.MarkFailed("Bulk export status poll failed.", DateTime.UtcNow);
        }

        return job;
    }

    private static (BulkExportJobsController Controller, Mock<IPausedBulkExportRunCloser> Closer) CreateController(BulkExportJob job)
    {
        var repository = new Mock<IBulkExportJobRepository>();
        repository.Setup(r => r.GetAsync(job.Id, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        var closer = new Mock<IPausedBulkExportRunCloser>();
        var controller = new BulkExportJobsController(
            repository.Object,
            new Mock<IFhirBulkExportClient>().Object,
            new Mock<ISourceConnectionRuntimeResolver>().Object,
            closer.Object,
            NullLogger<BulkExportJobsController>.Instance);
        return (controller, closer);
    }
}
