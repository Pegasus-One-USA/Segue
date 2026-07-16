using FHIRBridge.Application.Abstractions.Audit;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Enums;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// A destination with pipeline execution history must not be editable or deletable — only viewable. The gate
/// (ConfigurationService.EnsureDestinationHasNoExecutionHistoryAsync) is exercised here against a mocked
/// IConfigurationRepository, since neither InMemoryConfigurationRepository (always reports no history — there is
/// no in-memory store of executions) nor the API integration-test host (same in-memory repository path) can
/// produce a "has history" state to test against.
/// </summary>
public sealed class DestinationExecutionHistoryGateTests
{
    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly ConfigurationService _sut;
    private readonly DestinationConfiguration _destination = new(
        "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "warehouse-secret"), "dbo.Patients");

    public DestinationExecutionHistoryGateTests()
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.CurrentUser)
            .Returns(new CurrentUserInfo("admin", "admin@example.com", "Admin", ["Administrator"], true));

        _repository.Setup(x => x.GetDestinationAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_destination);

        _sut = new ConfigurationService(
            _repository.Object,
            Mock.Of<ISourceCapabilityRepository>(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            Mock.Of<IOperationalAuditService>(),
            currentUser.Object,
            Mock.Of<ISecretWriter>());
    }

    private static CreateDestinationConfigurationRequest UpdateRequest() =>
        new("Warehouse (renamed)", DestinationType.SqlServer, "kv", "warehouse-secret", "dbo.Patients");

    [Fact]
    public async Task Update_throws_when_destination_has_execution_history()
    {
        _repository.Setup(x => x.HasDestinationExecutionHistoryAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => _sut.UpdateDestinationConfigurationAsync(_destination.Id, UpdateRequest(), CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
        _repository.Verify(x => x.UpdateDestinationAsync(It.IsAny<DestinationConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Update_succeeds_when_destination_has_no_execution_history()
    {
        _repository.Setup(x => x.HasDestinationExecutionHistoryAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var result = await _sut.UpdateDestinationConfigurationAsync(_destination.Id, UpdateRequest(), CancellationToken.None);

        result.Name.Should().Be("Warehouse (renamed)");
        _repository.Verify(x => x.UpdateDestinationAsync(_destination, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Delete_throws_when_destination_has_execution_history()
    {
        _repository.Setup(x => x.HasDestinationExecutionHistoryAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var act = () => _sut.DeleteDestinationConfigurationAsync(_destination.Id, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
        _repository.Verify(x => x.RemoveDestinationAsync(It.IsAny<DestinationConfiguration>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Delete_removes_the_destination_when_it_has_no_execution_history()
    {
        _repository.Setup(x => x.HasDestinationExecutionHistoryAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await _sut.DeleteDestinationConfigurationAsync(_destination.Id, CancellationToken.None);

        _repository.Verify(x => x.RemoveDestinationAsync(_destination, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Paged_query_maps_repository_results_to_dtos()
    {
        var page = new PagedResult<DestinationConfiguration>([_destination], TotalCount: 1, Page: 1, PageSize: 25);
        _repository.Setup(x => x.GetDestinationsPagedAsync(It.IsAny<DestinationFilter>(), 1, 25, It.IsAny<CancellationToken>()))
            .ReturnsAsync(page);

        var result = await _sut.GetDestinationConfigurationsPagedAsync(
            new DestinationFilter(null, null, null), 1, 25, CancellationToken.None);

        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle(x => x.Id == _destination.Id && x.Name == "Warehouse");
    }
}
