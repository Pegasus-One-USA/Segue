using FHIRBridge.Application.Abstractions.Governance;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities.Governance;
using FHIRBridge.Infrastructure.Governance;
using FHIRBridge.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Moq;

namespace FHIRBridge.UnitTests.Governance;

public sealed class EfGovernanceQueryServiceSearchErrorLogsTests
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
