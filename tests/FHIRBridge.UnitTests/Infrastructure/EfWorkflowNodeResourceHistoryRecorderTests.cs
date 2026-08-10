using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Persistence.Workflows;
using FHIRBridge.Runtime.Domain.Workflows;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly InMemoryDatabaseRoot _root = new();
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
                Guid.NewGuid(), workflowRunId, succeededNodeRun.Id, "EpicSourceNode", "ResourceBatch", "{\"count\":1}", 1, DateTimeOffset.UtcNow));
            await context.SaveChangesAsync();
        }

        await using var readContext = CreateContext();
        var recorder = new EfWorkflowNodeResourceHistoryRecorder(readContext, PassthroughEncryptor());

        var result = await recorder.GetNodeRunHistoryPagedAsync(workflowRunId, page: 1, pageSize: 25, CancellationToken.None);

        result.TotalCount.Should().Be(2, "both nodes started, regardless of whether either wrote a payload");
        var failedEntry = result.Items.Single(x => x.WorkflowNodeRunId == failedNodeRun.Id);
        failedEntry.Status.Should().Be("Failed");
        failedEntry.ErrorMessage.Should().Be("Destination write failed: timeout.");
        failedEntry.PayloadJson.Should().BeNull();

        var succeededEntry = result.Items.Single(x => x.WorkflowNodeRunId == succeededNodeRun.Id);
        succeededEntry.Status.Should().Be("Succeeded");
        succeededEntry.ErrorMessage.Should().BeNull();
        succeededEntry.PayloadJson.Should().Be("{\"count\":1}");
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
        var recorder = new EfWorkflowNodeResourceHistoryRecorder(readContext, PassthroughEncryptor());

        var result = await recorder.GetNodeRunHistoryPagedAsync(workflowRunId, page: 1, pageSize: 25, CancellationToken.None);

        result.Items.Select(x => x.NodeType).Should().Equal("EpicSourceNode", "DestinationNode");
    }
}
