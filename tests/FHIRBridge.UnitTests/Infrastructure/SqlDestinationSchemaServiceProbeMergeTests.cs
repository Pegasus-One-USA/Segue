using FHIRBridge.Application.Abstractions.Destinations;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Destinations;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Moq;

namespace FHIRBridge.UnitTests.Infrastructure;

/// <summary>
/// Locks in the Test Connection counterpart to DestinationSecretPreservationTests' save-time
/// InheritSecretFromDestinationId coverage: an existing SQL-family connection forked by editing an unrelated
/// field (e.g. Require SSL) leaves the password blank, and Test Connection must inherit the stored credentials
/// via <see cref="ISqlConnectionSecretMerger"/> the same way AddDestinationConfigurationAsync does, rather than
/// probing with a blank password and getting an auth error. These tests can only prove the merge/resolve step
/// runs with the right inputs — the actual TCP probe still fails in this sandbox (no live database reachable),
/// same limitation as SqlDestinationSchemaServiceTests' other "reaches the connection stage" tests.
/// </summary>
public sealed class SqlDestinationSchemaServiceProbeMergeTests
{
    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();
    private readonly Mock<ISqlConnectionSecretMerger> _secretMerger = new();
    private readonly SqlDestinationSchemaService _sut;
    private readonly DestinationConfiguration _destination = new(
        "Warehouse", DestinationType.PostgreSql, new SecretReference("kv", "warehouse-secret"), "dbo.Patients");

    public SqlDestinationSchemaServiceProbeMergeTests()
    {
        _repository.Setup(x => x.GetDestinationAsync(_destination.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(_destination);

        _sut = new SqlDestinationSchemaService(_repository.Object, _secretProvider.Object, _secretMerger.Object);
    }

    [Fact]
    public async Task ProbeSchemaAsync_blank_password_with_ExistingDestinationId_resolves_and_merges_the_stored_secret()
    {
        _secretProvider.Setup(x => x.GetSecretAsync(_destination.SecretReference, It.IsAny<CancellationToken>()))
            .ReturnsAsync("Host=old;Username=admin;Password=hunter2;");
        _secretMerger
            .Setup(x => x.TryInheritCredentials(
                DestinationType.PostgreSql, "Host=old;Username=admin;Password=hunter2;", It.IsAny<string>()))
            .Returns("Host=new;SSL Mode=Require;Password=hunter2;");

        var request = new DestinationConnectionProbeRequest(
            DestinationType.PostgreSql, Server: "new", Database: "fhirbridge_output", RequireSsl: true,
            ExistingDestinationId: _destination.Id);

        // Fails at the (unreachable in this sandbox) connection stage — the assertions below only confirm the
        // merge step ran with the right inputs, not that the probe itself succeeded.
        await _sut.ProbeSchemaAsync(request, CancellationToken.None);

        _secretProvider.Verify(
            x => x.GetSecretAsync(_destination.SecretReference, It.IsAny<CancellationToken>()), Times.Once);
        _secretMerger.Verify(
            x => x.TryInheritCredentials(
                DestinationType.PostgreSql, "Host=old;Username=admin;Password=hunter2;", It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task ProbeSchemaAsync_non_blank_password_never_touches_the_merger_or_secret_provider()
    {
        var request = new DestinationConnectionProbeRequest(
            DestinationType.PostgreSql, Server: "new", Database: "fhirbridge_output", Password: "typed-by-user",
            ExistingDestinationId: _destination.Id);

        await _sut.ProbeSchemaAsync(request, CancellationToken.None);

        _secretProvider.Verify(
            x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
        _secretMerger.Verify(
            x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeSchemaAsync_blank_password_without_ExistingDestinationId_never_touches_the_merger()
    {
        var request = new DestinationConnectionProbeRequest(
            DestinationType.PostgreSql, Server: "new", Database: "fhirbridge_output");

        await _sut.ProbeSchemaAsync(request, CancellationToken.None);

        _repository.Verify(
            x => x.GetDestinationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _secretMerger.Verify(
            x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeSchemaAsync_falls_back_gracefully_when_the_referenced_destination_no_longer_exists()
    {
        var missingId = Guid.NewGuid();
        _repository.Setup(x => x.GetDestinationAsync(missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((DestinationConfiguration?)null);

        var request = new DestinationConnectionProbeRequest(
            DestinationType.PostgreSql, Server: "new", Database: "fhirbridge_output",
            ExistingDestinationId: missingId);

        var result = await _sut.ProbeSchemaAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        _secretMerger.Verify(
            x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    [Fact]
    public async Task ProbeSchemaAsync_falls_back_gracefully_when_the_old_secret_was_never_configured()
    {
        _secretProvider.Setup(x => x.GetSecretAsync(_destination.SecretReference, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SecretNotConfiguredException("warehouse-secret", "kv"));

        var request = new DestinationConnectionProbeRequest(
            DestinationType.PostgreSql, Server: "new", Database: "fhirbridge_output",
            ExistingDestinationId: _destination.Id);

        var result = await _sut.ProbeSchemaAsync(request, CancellationToken.None);

        result.Connected.Should().BeFalse();
        _secretMerger.Verify(
            x => x.TryInheritCredentials(It.IsAny<DestinationType>(), It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }
}
