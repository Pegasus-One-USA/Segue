using FHIRBridge.Application.Abstractions.Destinations;
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
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Configuration;

/// <summary>
/// Locks in the fix for a destination edit silently corrupting its stored secret: neither the destination list
/// dialog nor the wizard canvas flow ever re-displays a previously stored secret, so a re-save that doesn't
/// intend to change the secret still resubmits KeyVaultName/SecretName (required by
/// CreateDestinationConfigurationRequestValidator) alongside a blank InlineSecret. Only a non-blank InlineSecret
/// should ever cause the entity's SecretReference to change.
/// </summary>
public sealed class DestinationSecretPreservationTests
{
    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly Mock<ISecretWriter> _secretWriter = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();
    private readonly Mock<ISqlConnectionSecretMerger> _secretMerger = new();
    private readonly ConfigurationService _sut;
    private readonly DestinationConfiguration _destination = new(
        "Warehouse", DestinationType.SqlServer, new SecretReference("kv", "warehouse-secret"), "dbo.Patients");

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
            _secretProvider.Object,
            _secretMerger.Object,
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
    public async Task Update_with_blank_InlineSecret_preserves_the_existing_SecretReference()
    {
        // Simulates the real-world bug trigger: the form resubmits the entity's own KeyVaultName/SecretName
        // (validator requires non-empty) but a fabricated/stale value would previously have overwritten the
        // reference even though InlineSecret (the only unambiguous "replace it" signal) is blank.
        var request = new CreateDestinationConfigurationRequest(
            "Warehouse (renamed)", DestinationType.SqlServer, "some-other-vault", "some-other-secret", "dbo.Patients");

        await _sut.UpdateDestinationConfigurationAsync(_destination.Id, request, CancellationToken.None);

        _destination.SecretReference.KeyVaultName.Should().Be("kv");
        _destination.SecretReference.SecretName.Should().Be("warehouse-secret");
        _secretWriter.Verify(
            x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Update_with_non_blank_InlineSecret_replaces_the_SecretReference_and_writes_it()
    {
        var request = new CreateDestinationConfigurationRequest(
            "Warehouse (renamed)", DestinationType.SqlServer, "kv", "warehouse-secret", "dbo.Patients",
            InlineSecret: "Server=new;Password=hunter2;");

        await _sut.UpdateDestinationConfigurationAsync(_destination.Id, request, CancellationToken.None);

        _destination.SecretReference.KeyVaultName.Should().Be("kv");
        _destination.SecretReference.SecretName.Should().Be("warehouse-secret");
        _secretWriter.Verify(
            x => x.WriteSecretAsync(
                It.Is<SecretReference>(r => r.KeyVaultName == "kv" && r.SecretName == "warehouse-secret"),
                "Server=new;Password=hunter2;",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Add_with_non_blank_InlineSecret_writes_it_at_the_requested_reference()
    {
        var request = new CreateDestinationConfigurationRequest(
            "New Destination", DestinationType.SqlServer, "kv", "new-secret", "dbo.Orders",
            InlineSecret: "Server=x;Password=y;");

        await _sut.AddDestinationConfigurationAsync(request, CancellationToken.None);

        _secretWriter.Verify(
            x => x.WriteSecretAsync(
                It.Is<SecretReference>(r => r.KeyVaultName == "kv" && r.SecretName == "new-secret"),
                "Server=x;Password=y;",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── inherit credentials when forking off an existing SQL connection ─────────────────────────────

    [Fact]
    public async Task Add_with_InheritSecretFromDestinationId_writes_the_merger_s_output_not_the_blank_password_one()
    {
        _secretProvider.Setup(x => x.GetSecretAsync(_destination.SecretReference, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Server=old;User Id=admin;Password=hunter2;");
        _secretMerger
            .Setup(x => x.TryInheritCredentials(
                DestinationType.PostgreSql, "Server=old;User Id=admin;Password=hunter2;", "Host=new;SSL Mode=Require;Password=;"))
            .Returns("Host=new;SSL Mode=Require;Password=hunter2;");

        var request = new CreateDestinationConfigurationRequest(
            "Warehouse-1", DestinationType.PostgreSql, "kv", "warehouse-1-secret", "dbo.Patients",
            InlineSecret: "Host=new;SSL Mode=Require;Password=;",
            InheritSecretFromDestinationId: _destination.Id);

        await _sut.AddDestinationConfigurationAsync(request, CancellationToken.None);

        _secretWriter.Verify(
            x => x.WriteSecretAsync(
                It.IsAny<SecretReference>(), "Host=new;SSL Mode=Require;Password=hunter2;", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Add_with_InheritSecretFromDestinationId_falls_back_to_the_original_secret_when_the_merger_finds_nothing_to_inherit()
    {
        _secretProvider.Setup(x => x.GetSecretAsync(_destination.SecretReference, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Server=old;Password=hunter2;");
        _secretMerger
            .Setup(x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string?)null);

        var request = new CreateDestinationConfigurationRequest(
            "Warehouse-1", DestinationType.SqlServer, "kv", "warehouse-1-secret", "dbo.Patients",
            InlineSecret: "Server=new;Password=typed-by-user;",
            InheritSecretFromDestinationId: _destination.Id);

        await _sut.AddDestinationConfigurationAsync(request, CancellationToken.None);

        _secretWriter.Verify(
            x => x.WriteSecretAsync(
                It.IsAny<SecretReference>(), "Server=new;Password=typed-by-user;", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Add_with_InheritSecretFromDestinationId_falls_back_gracefully_when_the_old_secret_was_never_configured()
    {
        _secretProvider.Setup(x => x.GetSecretAsync(_destination.SecretReference, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SecretNotConfiguredException("warehouse-secret", "kv"));

        var request = new CreateDestinationConfigurationRequest(
            "Warehouse-1", DestinationType.PostgreSql, "kv", "warehouse-1-secret", "dbo.Patients",
            InlineSecret: "Host=new;Password=;",
            InheritSecretFromDestinationId: _destination.Id);

        await _sut.AddDestinationConfigurationAsync(request, CancellationToken.None);

        _secretMerger.Verify(
            x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _secretWriter.Verify(
            x => x.WriteSecretAsync(It.IsAny<SecretReference>(), "Host=new;Password=;", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Add_without_InheritSecretFromDestinationId_never_touches_the_merger()
    {
        var request = new CreateDestinationConfigurationRequest(
            "New Destination", DestinationType.PostgreSql, "kv", "new-secret", "dbo.Orders",
            InlineSecret: "Host=x;Password=y;");

        await _sut.AddDestinationConfigurationAsync(request, CancellationToken.None);

        _secretMerger.Verify(
            x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
        _secretProvider.Verify(
            x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
