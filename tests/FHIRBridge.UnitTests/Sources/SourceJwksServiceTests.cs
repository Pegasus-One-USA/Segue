using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Aggregates;
using FHIRBridge.Domain.Entities;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SourceJwksServiceTests
{
    private readonly Mock<ITenantConfigurationRepository> _tenantRepository = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();

    private static readonly Guid TenantId = Guid.NewGuid();

    private SourceJwksService Service() => new(
        _tenantRepository.Object,
        _secretProvider.Object,
        NullLogger<SourceJwksService>.Instance);

    private SourceConnection SeedSource(SecretReference? privateKey, string? keyId)
    {
        var tenant = new Tenant("Contoso Health", "contoso");
        var auth = new SourceAuthenticationConfiguration(
            AuthenticationType.SmartBackendServices, "client-1", "https://auth.example.com/token",
            ["system/*.read"], null, privateKey, keyId);
        var source = tenant.AddSourceConnection(
            "Epic Backend", SourceSystemType.Epic, "https://fhir.example.com", auth);

        _tenantRepository.Setup(x => x.GetByIdAsync(TenantId, It.IsAny<CancellationToken>())).ReturnsAsync(tenant);
        return source;
    }

    [Fact]
    public async Task GetPublicJwksAsync_publishes_the_public_key_matching_the_stored_private_key()
    {
        using var rsa = RSA.Create(2048);
        var keyReference = new SecretReference("vault", "epic-private-key");
        var source = SeedSource(keyReference, keyId: "key-2026");
        _secretProvider.Setup(x => x.GetSecretAsync(
                It.Is<SecretReference>(s => s.SecretName == "epic-private-key"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rsa.ExportPkcs8PrivateKeyPem());

        var jwks = await Service().GetPublicJwksAsync(TenantId, source.Id, CancellationToken.None);

        jwks.Keys.Should().ContainSingle();
        var key = jwks.Keys[0];
        key.KeyType.Should().Be("RSA");
        key.Use.Should().Be("sig");
        key.Algorithm.Should().Be("RS384");
        key.KeyId.Should().Be("key-2026");

        // n/e must be the base64url (unpadded) big-endian public parameters of the stored key.
        var parameters = rsa.ExportParameters(includePrivateParameters: false);
        key.Modulus.Should().Be(Base64UrlEncode(parameters.Modulus!));
        key.Exponent.Should().Be(Base64UrlEncode(parameters.Exponent!));
        key.Modulus.Should().NotContain("=").And.NotContain("+").And.NotContain("/");
    }

    [Fact]
    public async Task GetPublicJwksAsync_omits_the_kid_when_the_connection_has_no_key_id()
    {
        using var rsa = RSA.Create(2048);
        var source = SeedSource(new SecretReference("vault", "epic-private-key"), keyId: null);
        _secretProvider.Setup(x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rsa.ExportPkcs8PrivateKeyPem());

        var jwks = await Service().GetPublicJwksAsync(TenantId, source.Id, CancellationToken.None);

        jwks.Keys.Should().ContainSingle().Which.KeyId.Should().BeNull();
    }

    [Fact]
    public async Task GetPublicJwksAsync_returns_an_empty_set_when_no_asymmetric_key_is_configured()
    {
        var source = SeedSource(privateKey: null, keyId: null);

        var jwks = await Service().GetPublicJwksAsync(TenantId, source.Id, CancellationToken.None);

        jwks.Keys.Should().BeEmpty();
        _secretProvider.Verify(
            x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPublicJwksAsync_throws_for_an_unknown_source_connection()
    {
        SeedSource(new SecretReference("vault", "epic-private-key"), keyId: null);

        var act = () => Service().GetPublicJwksAsync(TenantId, Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
