using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Covers the fix for a real gap: a failed/cancelled node never wrote a <see cref="WorkflowNodeRunPayload"/>
/// (only the success path does), so the Execution History screen's old resources-only query silently omitted
/// it — indistinguishable from "never part of the workflow." GetNodeRunHistoryPagedAsync sources from
/// <see cref="WorkflowNodeRun"/> instead (written on every path), so a node that started always has a row.
/// </summary>
public sealed class EfWorkflowNodeResourceHistoryRecorderTests
{
    // static: xUnit builds a new instance of this class for EVERY test, and each distinct
    // InMemoryDatabaseRoot makes EF build another internal service provider — past twenty, EF raises
    // ManyServiceProvidersCreatedWarning as an error in whichever test happens to cross the line, which
    // reads as an unrelated failure elsewhere in the suite. Sharing one root costs no isolation: each test
    // still gets its own database via the unique _databaseName below. (Same pattern as WorkflowSqlStoreTests.)
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options;
        return new FHIRBridgeDbContext(options);
    }

    private static IPhiFieldEncryptor PassthroughEncryptor()
    {
        var mock = new Mock<IPhiFieldEncryptor>();
        mock.Setup(e => e.Encrypt(It.IsAny<string>())).Returns((string s) => s);
        mock.Setup(e => e.Decrypt(It.IsAny<string>())).Returns((string s) => s);
        return mock.Object;
    }

    [Fact]
    public async Task A_failed_node_appears_with_its_error_message_even_though_it_never_wrote_a_payload()
    {
        var workflowRunId = Guid.NewGuid();
        var succeededNodeRun = new WorkflowNodeRun(Guid.NewGuid(), workflowRunId, Guid.NewGuid(), "EpicSourceNode", 0, 0, DateTimeOffset.UtcNow);
        succeededNodeRun.Succeed("{}", DateTimeOffset.UtcNow);
        var failedNodeRun = new WorkflowNodeRun(Guid.NewGuid(), workflowRunId, Guid.NewGuid(), "MappingNode", 1, 0, DateTimeOffset.UtcNow);
        failedNodeRun.Fail("Destination write failed: timeout.", DateTimeOffset.UtcNow);

        await using (var context = CreateContext())
        {
            context.WorkflowNodeRuns.AddRange(succeededNodeRun, failedNodeRun);
            // Only the succeeded node ever gets a payload row — mirrors real RankedWorkflowOrchestrator behavior.
            context.WorkflowNodeRunPayloads.Add(new WorkflowNodeRunPayload(
                Guid.NewGuid(), workflowRunId, succeededNodeRun.Id, "EpicSourceNode", "ResourceBatch",
                1, "{\"Patient\":1}", null, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var recorder = new EfWorkflowNodeResourceHistoryRecorder(readContext, PassthroughEncryptor(), NullLogger<EfWorkflowNodeResourceHistoryRecorder>.Instance);

        var result = await recorder.GetNodeRunHistoryPagedAsync(workflowRunId, page: 1, pageSize: 25, CancellationToken.None);

        result.TotalCount.Should().Be(2, "both nodes started, regardless of whether either wrote a payload");
        var failedEntry = result.Items.Single(x => x.WorkflowNodeRunId == failedNodeRun.Id);
        failedEntry.Status.Should().Be("Failed");
        failedEntry.ErrorMessage.Should().Be("Destination write failed: timeout.");
        failedEntry.ItemCount.Should().BeNull();

        var succeededEntry = result.Items.Single(x => x.WorkflowNodeRunId == succeededNodeRun.Id);
        succeededEntry.Status.Should().Be("Succeeded");
        succeededEntry.ErrorMessage.Should().BeNull();
        succeededEntry.Contract.Should().Be("ResourceBatch");
        succeededEntry.ItemCount.Should().Be(1);
        succeededEntry.ResourceTypeCountsJson.Should().Be("{\"Patient\":1}");
    }

    [Fact]
    public async Task GetNodeRunPayloadAsync_returns_counts_not_content_for_one_node_run()
    {
        var workflowRunId = Guid.NewGuid();
        var nodeRunId = Guid.NewGuid();

        await using (var context = CreateContext())
        {
            context.WorkflowNodeRunPayloads.Add(new WorkflowNodeRunPayload(
                Guid.NewGuid(), workflowRunId, nodeRunId, "EpicSourceNode", "ResourceBatch",
                1, "{\"Patient\":1}", null, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var recorder = new EfWorkflowNodeResourceHistoryRecorder(readContext, PassthroughEncryptor(), NullLogger<EfWorkflowNodeResourceHistoryRecorder>.Instance);

        var result = await recorder.GetNodeRunPayloadAsync(workflowRunId, nodeRunId, CancellationToken.None);

        result.Should().NotBeNull();
        result!.Contract.Should().Be("ResourceBatch");
        result.ItemCount.Should().Be(1);
        result.ResourceTypeCountsJson.Should().Be("{\"Patient\":1}");
    }

    [Fact]
    public async Task GetNodeRunPayloadAsync_returns_null_when_the_node_run_never_wrote_a_payload()
    {
        await using var readContext = CreateContext();
        var recorder = new EfWorkflowNodeResourceHistoryRecorder(readContext, PassthroughEncryptor(), NullLogger<EfWorkflowNodeResourceHistoryRecorder>.Instance);

        var result = await recorder.GetNodeRunPayloadAsync(Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task Node_runs_are_ordered_by_rank_then_subrank()
    {
        var workflowRunId = Guid.NewGuid();
        var second = new WorkflowNodeRun(Guid.NewGuid(), workflowRunId, Guid.NewGuid(), "DestinationNode", 2, 0, DateTimeOffset.UtcNow);
        second.Succeed("{}", DateTimeOffset.UtcNow);
        var first = new WorkflowNodeRun(Guid.NewGuid(), workflowRunId, Guid.NewGuid(), "EpicSourceNode", 0, 0, DateTimeOffset.UtcNow);
        first.Succeed("{}", DateTimeOffset.UtcNow);

        await using (var context = CreateContext())
        {
            context.WorkflowNodeRuns.AddRange(second, first);
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var recorder = new EfWorkflowNodeResourceHistoryRecorder(readContext, PassthroughEncryptor(), NullLogger<EfWorkflowNodeResourceHistoryRecorder>.Instance);

        var result = await recorder.GetNodeRunHistoryPagedAsync(workflowRunId, page: 1, pageSize: 25, CancellationToken.None);

        result.Items.Select(x => x.NodeType).Should().Equal("EpicSourceNode", "DestinationNode");
    }
}
