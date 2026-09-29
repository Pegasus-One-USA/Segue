using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.UnitTests.Infrastructure;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class EfGovernanceQueryServiceSearchErrorLogsTests
{
    // Routed through SharedInMemoryDatabase (one process-wide internal service provider) rather than a
    // per-class root: EF builds a distinct internal IServiceProvider per distinct options configuration and
    // raises ManyServiceProvidersCreatedWarning as an error past twenty in one process, in whichever test
    // happens to cross the line — which reads as an unrelated failure elsewhere in the suite. Isolation still
    // comes from this class's own unique _databaseName.
    private readonly string _databaseName = SharedInMemoryDatabase.NewDatabaseName();

    private FHIRBridgeDbContext CreateContext() =>
        new(SharedInMemoryDatabase.Options<FHIRBridgeDbContext>(_databaseName));

    private static EfGovernanceQueryService CreateSut(FHIRBridgeDbContext context) =>
        new(
            context,
            Mock.Of<IConfiguredPipelineRunRepository>(),
            Enumerable.Empty<IPurgeableStore>(),
            Mock.Of<IRetentionPolicyService>());

    [Fact]
    public async Task SearchErrorLogsAsync_WithNoSeverityFilter_ExcludesInformationalRows()
    {
        await using var context = CreateContext();
        context.ErrorLogs.AddRange(
            new ErrorLog(Guid.NewGuid(), DateTime.UtcNow, "Error", "InvalidOperationException", "Boom", null, "Api", "corr-1"),
            new ErrorLog(Guid.NewGuid(), DateTime.UtcNow, "Informational", "InvalidOperationException", "Wrong password", null, "Api", "corr-2"));
        await context.SaveChangesAsync();

        var sut = CreateSut(context);

        var result = await sut.SearchErrorLogsAsync(new ErrorLogSearch(), CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items.Single().Severity.Should().Be("Error");
    }

    [Fact]
    public async Task SearchErrorLogsAsync_WithInformationalSeverityFilter_ReturnsInformationalRows()
    {
        await using var context = CreateContext();
        context.ErrorLogs.AddRange(
            new ErrorLog(Guid.NewGuid(), DateTime.UtcNow, "Error", "InvalidOperationException", "Boom", null, "Api", "corr-1"),
            new ErrorLog(Guid.NewGuid(), DateTime.UtcNow, "Informational", "InvalidOperationException", "Wrong password", null, "Api", "corr-2"));
        await context.SaveChangesAsync();

        var sut = CreateSut(context);

        var result = await sut.SearchErrorLogsAsync(new ErrorLogSearch(Severity: "Informational"), CancellationToken.None);

        result.Items.Should().ContainSingle();
        result.Items.Single().Severity.Should().Be("Informational");
    }
}
