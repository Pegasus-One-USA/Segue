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

public sealed class EfGovernanceQueryServiceCorrelationSearchSmartLaunchTests
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

    private static EfGovernanceQueryService CreateSut(FHIRBridgeDbContext context) =>
        new(
            context,
            Mock.Of<IConfiguredPipelineRunRepository>(),
            Enumerable.Empty<IPurgeableStore>(),
            Mock.Of<IRetentionPolicyService>());

    [Fact]
    public async Task GetCorrelationSearchResultAsync_IncludesSmartLaunchLogsMatchingCorrelationId()
    {
        await using var context = CreateContext();
        const string correlationId = "corr-smart-1";
        context.SmartLaunchLogs.AddRange(
            new SmartLaunchLog(
                Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), "Epic Sandbox", "EhrLaunch", success: true,
                failureReason: null, grantedScope: "patient/*.read", patientContextGranted: true,
                tokenCacheKeyHash: "abc123", correlationId: correlationId),
            new SmartLaunchLog(
                Guid.NewGuid(), DateTime.UtcNow, Guid.NewGuid(), "Epic Sandbox", "Standalone", success: false,
                failureReason: "denied", correlationId: "different-correlation-id"));
        await context.SaveChangesAsync();

        var sut = CreateSut(context);

        var result = await sut.GetCorrelationSearchResultAsync(correlationId, CancellationToken.None);

        result.SmartLaunchLogs.Should().ContainSingle();
        result.SmartLaunchLogs.Single().SourceName.Should().Be("Epic Sandbox");
        result.SmartLaunchLogs.Single().CorrelationId.Should().Be(correlationId);
        result.TotalCount.Should().Be(1);
    }
}
