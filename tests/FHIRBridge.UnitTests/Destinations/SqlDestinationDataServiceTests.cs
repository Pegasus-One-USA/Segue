using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Destinations;

public sealed class SqlDestinationDataServiceTests
{
    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();

    private SqlDestinationDataService Service() => new(_repository.Object, _secretProvider.Object);

    // The stored destination target can carry a ";mode=<writeMode>" write directive (e.g. "dbo.Patient;mode=upsert")
    // or a "?..." query suffix. ParseTarget must strip either before validating the SQL identifier, otherwise the
    // portal "View data" preview fails with e.g. "'Patient;mode=upsert' is not a valid SQL identifier."
    [Theory]
    [InlineData("dbo.Patient;mode=upsert")]
    [InlineData("EpicGroupPatients;mode=upsert")]
    [InlineData("dbo.Patient?mode=upsert")]
    public async Task ReadSampleAsync_strips_write_directive_or_query_suffix_from_target(string target)
    {
        var destinationId = Guid.NewGuid();
        var destination = new DestinationConfiguration(
            "SQL Production",
            DestinationType.SqlServer,
            new SecretReference("dev-local", "sql-output-connection-string"),
            target);

        _repository
            .Setup(r => r.GetDestinationAsync(destinationId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(destination);

        // A deliberately invalid connection string so the method fails fast at connect — AFTER ParseTarget. Reaching
        // a connection-level error (rather than the identifier error) proves the ";mode=..."/"?..." suffix was stripped.
        _secretProvider
            .Setup(s => s.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("not-a-valid-connection-string");

        var result = await Service().ReadSampleAsync(
            destinationId, "Patient", 10, new[] { Guid.NewGuid() }, CancellationToken.None);

        result.Error.Should().NotBeNull();
        result.Error.Should().NotContain("is not a valid SQL identifier");
    }
}
