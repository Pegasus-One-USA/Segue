using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
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
    private readonly Mock<IConfigurationRepository> _configurationRepository = new();
    private readonly Mock<ISecretProvider> _secretProvider = new();

    private SourceJwksService Service() => new(
        _configurationRepository.Object,
        _secretProvider.Object,
        NullLogger<SourceJwksService>.Instance);

    private SourceConnection SeedSource(SecretReference? privateKey, string? keyId)
    {
        var auth = new SourceAuthenticationConfiguration(
            AuthenticationType.SmartBackendServices, "client-1", "https://auth.example.com/token",
            ["system/*.read"], null, privateKey, keyId);
        var source = new SourceConnection(
            "Epic Backend", SourceSystemType.Epic, "https://fhir.example.com", auth);

        _configurationRepository
            .Setup(x => x.GetSourceConnectionAsync(source.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(source);
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

        var jwks = await Service().GetPublicJwksAsync(source.Id, CancellationToken.None);

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

        var jwks = await Service().GetPublicJwksAsync(source.Id, CancellationToken.None);

        jwks.Keys.Should().ContainSingle().Which.KeyId.Should().BeNull();
    }

    [Fact]
    public async Task GetPublicJwksAsync_returns_an_empty_set_when_no_asymmetric_key_is_configured()
    {
        var source = SeedSource(privateKey: null, keyId: null);

        var jwks = await Service().GetPublicJwksAsync(source.Id, CancellationToken.None);

        jwks.Keys.Should().BeEmpty();
        _secretProvider.Verify(
            x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetPublicJwksAsync_throws_for_an_unknown_source_connection()
    {
        SeedSource(new SecretReference("vault", "epic-private-key"), keyId: null);

        var act = () => Service().GetPublicJwksAsync(Guid.NewGuid(), CancellationToken.None);

        await act.Should().ThrowAsync<NotFoundException>();
    }

    // The connection stores only a reference to the key, so it can be saved (and its JWKS URL registered with the
    // EHR) while the slot it points at holds an unset appsettings placeholder. Before the guard this reached
    // RSA.ImportFromPem and surfaced as "No supported key formats were found... not the path to such a file" —
    // naming neither the connection nor the secret, and mapped to a 400 with a generic message.
    [Theory]
    [InlineData("SET_VIA_DOTNET_USER_SECRETS")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/etc/keys/epic.pem")]
    public async Task GetPublicJwksAsync_names_the_connection_and_secret_when_the_reference_holds_no_pem(
        string resolvedValue)
    {
        var source = SeedSource(new SecretReference("dev-local", "epic-backend-private-key"), keyId: "key-2026");
        _secretProvider.Setup(x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolvedValue);

        var act = () => Service().GetPublicJwksAsync(source.Id, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should()
            .Contain("Epic Backend")
            .And.Contain("epic-backend-private-key")
            .And.Contain("dev-local");

        // Never echo the resolved value — it is key material whenever the same path succeeds.
        if (!string.IsNullOrWhiteSpace(resolvedValue))
        {
            thrown.Which.Message.Should().NotContain(resolvedValue);
        }
    }

    [Fact]
    public async Task GetPublicJwksAsync_reports_a_pem_shaped_key_that_is_not_an_importable_rsa_key()
    {
        var source = SeedSource(new SecretReference("signing-keys", "epic-private-key-abc"), keyId: "key-2026");
        _secretProvider.Setup(x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("-----BEGIN PRIVATE KEY-----\ntruncated\n-----END PRIVATE KEY-----");

        var act = () => Service().GetPublicJwksAsync(source.Id, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().Contain("Epic Backend").And.Contain("epic-private-key-abc");
    }

    // A public-key PEM imports without error but has no private component; it must not be mistaken for a signing
    // key, and the failure must still name where it came from.
    [Fact]
    public async Task GetPublicJwksAsync_rejects_a_public_key_pem_stored_at_the_signing_key_reference()
    {
        using var rsa = RSA.Create(2048);
        var source = SeedSource(new SecretReference("signing-keys", "epic-private-key-abc"), keyId: "key-2026");
        _secretProvider.Setup(x => x.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(rsa.ExportSubjectPublicKeyInfoPem());

        var act = () => Service().GetPublicJwksAsync(source.Id, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
