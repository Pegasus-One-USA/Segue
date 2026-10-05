using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class EfGovernanceQueryServiceDashboardTests
{
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private FHIRBridgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FHIRBridgeDbContext>().UseInMemoryDatabase(_databaseName, _root).Options);

    private static EfGovernanceQueryService CreateSut(FHIRBridgeDbContext context) =>
        new(context, Mock.Of<IConfiguredPipelineRunRepository>(), Enumerable.Empty<IPurgeableStore>(), Mock.Of<IRetentionPolicyService>());

    private static ErrorLog Row(DateTime at, string severity, string type, string module, string message, string correlation) =>
        new(Guid.NewGuid(), at, severity, type, message, null, module, correlation);

    [Fact]
    public async Task Dashboard_counts_the_whole_period_and_groups_in_the_database()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.ErrorLogs.AddRange(
            Row(now.AddHours(-1), "Error", "TimeoutException", "Api", "Timed out calling the source", "c1"),
            Row(now.AddHours(-2), "Error", "TimeoutException", "Api", "Timed out calling the source", "c1"),
            Row(now.AddDays(-3), "Critical", "IOException", "Worker", "Disk full", "c2"),
            Row(now.AddHours(-1), "Information", "Info", "Api", "just a log line", "c3"),
            Row(now.AddHours(-1), "WorkflowDebug", "Trace", "Api", "step", "c4"),
            Row(now.AddHours(-1), "Informational", "Expected", "Api", "wrong password", "c5"));
        await context.SaveChangesAsync();

        var dashboard = await CreateSut(context).GetErrorDashboardAsync(now.AddDays(-14), now.AddMinutes(1), null, null, CancellationToken.None);

        dashboard.Total.Should().Be(3);
        dashboard.Critical.Should().Be(1);
        dashboard.Last24Hours.Should().Be(2);
        dashboard.Truncated.Should().BeFalse();
        dashboard.BySeverity.Should().Contain(x => x.Key == "Error" && x.Count == 2);
        dashboard.ByModule.Should().Contain(x => x.Key == "Api" && x.Count == 2);
        dashboard.ByDay.Sum(d => d.Count).Should().Be(3);
        var top = dashboard.TopSignatures.First();
        top.ExceptionType.Should().Be("TimeoutException");
        top.Count.Should().Be(2);
        top.SampleMessage.Should().Be("Timed out calling the source");
    }

    [Fact]
    public async Task Run_summaries_are_fetched_for_many_ids_at_once()
    {
        await using var context = CreateContext();
        var now = DateTime.UtcNow;
        context.ErrorLogs.AddRange(
            Row(now, "Error", "A", "Api", "first", "run-1"),
            Row(now, "Error", "B", "Api", "second", "run-2"),
            Row(now, "Error", "C", "Api", "other", "run-3"));
        await context.SaveChangesAsync();

        var runs = await CreateSut(context).GetCorrelationRunSummariesAsync(["run-1", "run-2"], CancellationToken.None);

        runs.Keys.Should().BeEquivalentTo("run-1", "run-2");
        runs["run-1"].Errors.Should().ContainSingle().Which.Message.Should().Be("first");
        runs["run-2"].Errors.Should().ContainSingle().Which.Message.Should().Be("second");
        runs["run-1"].AuditLogs.Should().BeEmpty();
    }
}
