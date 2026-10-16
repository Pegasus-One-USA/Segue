using System.Net;
using System.Text.Json;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Runtime.Application.Workflows.Storage;
using FHIRBridge.Runtime.Domain.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FHIRBridge.Api.IntegrationTests.Tests;

/// <summary>
/// The Bulk Data Status popup reads <c>/workflow-runs/{id}/bulk-export-status</c>. A job that ended Failed or Cancelled
/// is final for this system — nothing polls it again — so the popup must report that stored outcome, and the run that
/// was paused on it must be closed rather than left "Running" for ever.
/// </summary>
[Collection("ApiTests")]
public sealed class BulkExportStatusLifecycleTests(ApiFixture f)
{
    [Fact]
    public async Task A_failed_job_reports_its_stored_failure_and_every_type_as_failed()
    {
        var (run, job) = await SeedPausedRunAsync();
        job.MarkKickedOff("https://source.example.test/fhir/$export-poll-location?job_id=1");
        job.MarkFailed("Bulk export did not complete after 120 status polls.", DateTime.UtcNow);
        await Jobs.UpdateAsync(job, CancellationToken.None);

        var resp = await f.AdminClient.GetAsync($"/api/v1/workflow-runs/{run.Id}/bulk-export-status");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Failed", body.GetProperty("status").GetString());
        Assert.Equal("Bulk export did not complete after 120 status polls.", body.GetProperty("errorMessage").GetString());
        // The popup's Elapsed ends at the stored end, which must arrive as an unambiguous UTC instant.
        var completedOnUtc = body.GetProperty("completedOnUtc").GetString();
        Assert.NotNull(completedOnUtc);
        Assert.EndsWith("Z", completedOnUtc);
        Assert.Equal(job.CompletedOnUtc, DateTimeOffset.Parse(completedOnUtc).UtcDateTime);
        var types = body.GetProperty("resourceTypes").EnumerateArray()
            .Select(t => (t.GetProperty("resourceType").GetString(), t.GetProperty("state").GetString()))
            .ToList();
        Assert.Equal([("Patient", "Failed"), ("Encounter", "Failed")], types);
    }

    [Fact]
    public async Task Cancelling_the_job_closes_its_paused_run_as_Cancelled_and_the_popup_reports_it()
    {
        var (run, job) = await SeedPausedRunAsync();
        job.MarkKickedOff("https://source.example.test/fhir/$export-poll-location?job_id=2");
        await Jobs.UpdateAsync(job, CancellationToken.None);

        // The seeded source connection does not exist, so the cancel is local only: no source server is called.
        var cancel = await f.AdminClient.PostAsync($"/api/v1/bulk-export-jobs/{job.Id}/cancel", content: null);

        Assert.True(cancel.StatusCode == HttpStatusCode.NoContent, await cancel.Content.ReadAsStringAsync());
        var saved = await Runs.GetAsync(run.Id, CancellationToken.None);
        Assert.Equal(WorkflowRunStatus.Cancelled, saved!.Status);
        Assert.NotNull(saved.CompletedAt);

        var resp = await f.AdminClient.GetAsync($"/api/v1/workflow-runs/{run.Id}/bulk-export-status");
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("Cancelled", body.GetProperty("status").GetString());
        Assert.All(body.GetProperty("resourceTypes").EnumerateArray(),
            t => Assert.Equal("Cancelled", t.GetProperty("state").GetString()));
        Assert.NotNull(body.GetProperty("completedOnUtc").GetString());
    }

    [Fact]
    public async Task Cancelling_an_already_cancelled_job_still_closes_a_run_left_paused()
    {
        // PR #247 review: a cancel whose run close failed returned 500 after the job was already saved Cancelled; a retry
        // then hit "already cancelled" and the run stayed paused for ever. The retry now closes it.
        var (run, job) = await SeedPausedRunAsync();
        job.MarkCancelled(DateTime.UtcNow);
        await Jobs.UpdateAsync(job, CancellationToken.None);

        var cancel = await f.AdminClient.PostAsync($"/api/v1/bulk-export-jobs/{job.Id}/cancel", content: null);

        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        var saved = await Runs.GetAsync(run.Id, CancellationToken.None);
        Assert.Equal(WorkflowRunStatus.Cancelled, saved!.Status);
    }

    private IWorkflowRunStore Runs => f.Services.GetRequiredService<IWorkflowRunStore>();

    private IBulkExportJobRepository Jobs => f.Services.GetRequiredService<IBulkExportJobRepository>();

    private async Task<(WorkflowRun Run, BulkExportJob Job)> SeedPausedRunAsync()
    {
        var run = new WorkflowRun(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, triggerType: "Manual", correlationId: "it-bulk");
        run.AwaitBulkExport("job-1");
        await Runs.SaveAsync(run, CancellationToken.None);

        var job = new BulkExportJob(
            Guid.NewGuid(), BulkExportJobSourcePath.WorkflowNode, sourceConnectionId: Guid.NewGuid(), sourceConfigurationId: null,
            exportRequestJson: "{}", kickedOffOnUtc: DateTime.UtcNow, workflowRunId: run.Id, workflowNodeId: Guid.NewGuid(),
            requestedResourceTypesJson: "[\"Patient\",\"Encounter\"]");
        await Jobs.AddAsync(job, CancellationToken.None);
        return (run, job);
    }
}
