using FHIRBridge.Application.Abstractions.Mapping;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Abstractions.Sources;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Services;
using FHIRBridge.Application.Validation;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Neither destination-editing surface in the portal ever re-displays a previously stored secret, so
/// KeyVaultName/SecretName on a re-save request are not a reliable signal of intent — only a non-blank
/// InlineSecret means "replace it". Regression coverage for UpdateDestinationConfigurationAsync preserving the
/// entity's existing SecretReference whenever InlineSecret is blank, regardless of what KeyVaultName/SecretName
/// the request carries (a stale value, or — as the destination wizard canvas flow does today — a freshly
/// fabricated one).
/// </summary>
public sealed class DestinationSecretPreservationTests
{
    private static readonly SecretReference ExistingReference = new("kv", "warehouse-secret");

    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly Mock<ISecretWriter> _secretWriter = new();
    private readonly ConfigurationService _sut;
    private readonly DestinationConfiguration _destination =
        new("Warehouse", DestinationType.SqlServer, ExistingReference, "dbo.Patients");

    public DestinationSecretPreservationTests()
    {
        _repository.Setup(x => x.GetDestinationAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_destination);
        _repository.Setup(x => x.HasDestinationExecutionHistoryAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _sut = new ConfigurationService(
            _repository.Object,
            Mock.Of<ISourceCapabilityRepository>(),
            Mock.Of<ISourceCapabilityDiscoveryService>(),
            _secretWriter.Object,
            new FHIRBridge.UnitTests.Security.PassthroughTenantSecretVaultResolver(),
            Mock.Of<IParentReferenceResolver>(),
            new CreateMappingProfileRequestValidator(
                new FHIRBridge.UnitTests.Validation.NoOpDestinationSchemaService(),
                _repository.Object,
                new FHIRBridge.UnitTests.Validation.NoOpEffectiveRuleResolver()),
            new CreateDestinationConfigurationRequestValidator(),
            new FHIRBridge.UnitTests.Security.PassthroughUserDisplayNameResolver(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ConfigurationService>.Instance);
    }

    [Fact]
    public async Task Update_without_inline_secret_preserves_the_existing_reference_even_if_the_request_sends_a_different_one()
    {
        var request = new CreateDestinationConfigurationRequest(
            "Warehouse (renamed)", DestinationType.SqlServer, "some-other-vault", "some-other-secret", "dbo.Patients");

        var result = await _sut.UpdateDestinationConfigurationAsync(_destination.Id, request, CancellationToken.None);

        result.KeyVaultName.Should().Be(ExistingReference.KeyVaultName);
        result.SecretName.Should().Be(ExistingReference.SecretName);
        _destination.SecretReference.Should().Be(ExistingReference);
        _secretWriter.Verify(
            w => w.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Update_with_an_inline_secret_writes_it_and_repoints_the_reference()
    {
        var request = new CreateDestinationConfigurationRequest(
            "Warehouse (renamed)", DestinationType.SqlServer, "some-other-vault", "some-other-secret", "dbo.Patients",
            InlineSecret: "new-connection-string");

        var result = await _sut.UpdateDestinationConfigurationAsync(_destination.Id, request, CancellationToken.None);

        result.KeyVaultName.Should().Be("some-other-vault");
        result.SecretName.Should().Be("some-other-secret");
        _secretWriter.Verify(
            w => w.WriteSecretAsync(
                new SecretReference("some-other-vault", "some-other-secret"),
                "new-connection-string",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
