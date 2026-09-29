using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Services;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

/// <summary>
/// Regression coverage for the stale-signing-key-reference bug: SourceConnectionRuntimeResolver must resolve a
/// PrivateKey secret only when AuthenticationType is SmartBackendServices, not merely when the field happens to be
/// populated — a leftover reference from a prior SmartBackendServices configuration (PreserveSecretsIfBlank in
/// ConfigurationService never clears a field the request left blank) must not make every run fail trying to fetch
/// a secret the connection's current auth method doesn't use.
/// </summary>
public sealed class SourceConnectionRuntimeResolverAuthGuardTests
{
    private readonly Mock<IConfigurationRepository> _repository = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();

    private SourceConnectionRuntimeResolver Service() => new(
        _repository.Object,
        _secretProvider.Object,
        Mock.Of<IScopeGeneratorService>(),
        accessTokenProvider: null,
        logger: NullLogger<SourceConnectionRuntimeResolver>.Instance);

    private SourceConnection SeedSource(AuthenticationType authenticationType, SecretReference? privateKey)
    {
        var auth = new SourceAuthenticationConfiguration(
            authenticationType, "client-1", "https://auth.example.com/token",
            ["system/Patient.read"], null, privateKey, keyId: "key-1");
        var source = new SourceConnection(
            "Athena Backend", SourceSystemType.Athenahealth, "https://api.athenahealth.com/fhir/r4", auth);

        _repository
            .Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
        return source;
    }

    [Fact]
    public async Task Leftover_PrivateKey_reference_is_never_resolved_when_AuthenticationType_is_not_SmartBackendServices()
    {
        var staleKeyReference = new SecretReference("signing-keys", "athenahealth-private-key-abc123");
        var source = SeedSource(AuthenticationType.OAuthClientCredentials, staleKeyReference);

        var config = await Service().ResolveAsync(
            source.Id, searchParameters: null, targetPatientId: null, CancellationToken.None);

        config.Should().NotBeNull();
        config!.PrivateKeyPem.Should().BeNull();
        _secretProvider.Verify(
            x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task PrivateKey_is_resolved_when_AuthenticationType_is_SmartBackendServices()
    {
        var keyReference = new SecretReference("vault", "athenahealth-private-key-abc123");
        var source = SeedSource(AuthenticationType.SmartBackendServices, keyReference);
        _secretProvider
            .Setup(x => x.GetSecretAsync(
                It.Is<SecretReference>(r => r.SecretName == "athenahealth-private-key-abc123"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync("-----BEGIN PRIVATE KEY-----\nMII=\n-----END PRIVATE KEY-----");

        var config = await Service().ResolveAsync(
            source.Id, searchParameters: null, targetPatientId: null, CancellationToken.None);

        config.Should().NotBeNull();
        config!.PrivateKeyPem.Should().NotBeNull();
        _secretProvider.Verify(
            x => x.GetSecretAsync(
                It.Is<SecretReference>(r => r.SecretName == "athenahealth-private-key-abc123"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
