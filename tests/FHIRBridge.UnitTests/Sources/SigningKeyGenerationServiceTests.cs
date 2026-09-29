using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Enums;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Sources;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Sources;

public sealed class SigningKeyGenerationServiceTests
{
    private readonly Mock<ISecretWriter> _secretWriter = new();
    private readonly Mock<ITenantSecretVaultResolver> _vaultResolver = new();

    public SigningKeyGenerationServiceTests()
    {
        _vaultResolver
            .Setup(x => x.ResolveVaultName(It.IsAny<string>()))
            .Returns<string>(requested => requested);
    }

    private SigningKeyGenerationService Service() => new(
        _secretWriter.Object,
        _vaultResolver.Object,
        NullLogger<SigningKeyGenerationService>.Instance);

    [Fact]
    public async Task GenerateAsync_writes_a_real_RS384_private_key_to_the_secret_store_and_never_returns_it()
    {
        SecretReference? writtenReference = null;
        string? writtenPrivateKeyPem = null;
        _secretWriter
            .Setup(x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SecretReference, string, CancellationToken>((reference, value, _) =>
            {
                writtenReference = reference;
                writtenPrivateKeyPem = value;
            })
            .Returns(Task.CompletedTask);

        var result = await Service().GenerateAsync(SourceSystemType.Epic, CancellationToken.None);

        result.Algorithm.Should().Be("RS384");
        result.KeyId.Should().NotBeNullOrWhiteSpace();
        result.KeyVaultName.Should().NotBeNullOrWhiteSpace();
        result.SecretName.Should().NotBeNullOrWhiteSpace();
        result.SecretName.Should().StartWith("epic-private-key-");

        writtenReference.Should().NotBeNull();
        writtenReference!.KeyVaultName.Should().Be(result.KeyVaultName);
        writtenReference.SecretName.Should().Be(result.SecretName);

        // The written value must be a real, importable 2048-bit RSA private key — not a placeholder string.
        writtenPrivateKeyPem.Should().NotBeNullOrWhiteSpace();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(writtenPrivateKeyPem);
        rsa.KeySize.Should().Be(2048);

        // GeneratedSigningKeyDto has no field capable of carrying the private key — the type system itself
        // guarantees it never leaves this service.
        typeof(FHIRBridge.Application.DTOs.GeneratedSigningKeyDto).GetProperties()
            .Select(p => p.Name)
            .Should().BeEquivalentTo(["KeyId", "KeyVaultName", "SecretName", "Algorithm"]);
    }

    [Fact]
    public async Task GenerateAsync_gives_every_call_its_own_unique_secret_name()
    {
        _secretWriter
            .Setup(x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var first = await Service().GenerateAsync(SourceSystemType.Epic, CancellationToken.None);
        var second = await Service().GenerateAsync(SourceSystemType.Epic, CancellationToken.None);

        first.SecretName.Should().NotBe(second.SecretName);
        first.KeyId.Should().NotBe(second.KeyId);
    }

    [Fact]
    public async Task GenerateAsync_names_the_secret_after_the_actual_vendor_not_always_epic()
    {
        _secretWriter
            .Setup(x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await Service().GenerateAsync(SourceSystemType.Athenahealth, CancellationToken.None);

        result.SecretName.Should().StartWith("athenahealth-private-key-");
    }

    [Fact]
    public async Task ImportAsync_accepts_a_valid_PKCS8_RSA_private_key_and_stores_it_verbatim_reimportable()
    {
        using var rsa = RSA.Create(2048);
        var pkcs8Pem = rsa.ExportPkcs8PrivateKeyPem();

        string? writtenPrivateKeyPem = null;
        _secretWriter
            .Setup(x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<SecretReference, string, CancellationToken>((_, value, _) => writtenPrivateKeyPem = value)
            .Returns(Task.CompletedTask);

        var result = await Service().ImportAsync(pkcs8Pem, SourceSystemType.Epic, CancellationToken.None);

        result.Algorithm.Should().Be("RS384");
        writtenPrivateKeyPem.Should().NotBeNullOrWhiteSpace();
        using var reimported = RSA.Create();
        reimported.ImportFromPem(writtenPrivateKeyPem);
        reimported.KeySize.Should().Be(2048);
    }

    [Fact]
    public async Task ImportAsync_accepts_a_valid_PKCS1_RSA_private_key()
    {
        using var rsa = RSA.Create(2048);
        var pkcs1Pem = rsa.ExportRSAPrivateKeyPem();
        _secretWriter
            .Setup(x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await Service().ImportAsync(pkcs1Pem, SourceSystemType.Epic, CancellationToken.None);

        result.Algorithm.Should().Be("RS384");
    }

    [Fact]
    public async Task ImportAsync_rejects_garbage_input_with_a_business_rule_exception()
    {
        var act = () => Service().ImportAsync("not a real key", SourceSystemType.Epic, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
        _secretWriter.Verify(
            x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ImportAsync_rejects_a_blank_value()
    {
        var act = () => Service().ImportAsync("   ", SourceSystemType.Epic, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task ImportAsync_rejects_a_public_key_pem()
    {
        using var rsa = RSA.Create(2048);
        var publicKeyPem = rsa.ExportSubjectPublicKeyInfoPem();

        var act = () => Service().ImportAsync(publicKeyPem, SourceSystemType.Epic, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task ImportAsync_rejects_a_non_RSA_EC_private_key()
    {
        using var ecdsa = ECDsa.Create();
        var ecPkcs8Pem = ecdsa.ExportPkcs8PrivateKeyPem();

        var act = () => Service().ImportAsync(ecPkcs8Pem, SourceSystemType.Epic, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task ImportAsync_rejects_a_key_weaker_than_2048_bits()
    {
        using var rsa = RSA.Create(1024);
        var weakPem = rsa.ExportPkcs8PrivateKeyPem();

        var act = () => Service().ImportAsync(weakPem, SourceSystemType.Epic, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>().WithMessage("*below the minimum required*");
    }

    [Fact]
    public async Task ImportAsync_rejects_an_encrypted_private_key()
    {
        using var rsa = RSA.Create(2048);
        var encryptedPem = rsa.ExportEncryptedPkcs8PrivateKeyPem(
            "correct horse battery staple",
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 100_000));

        var act = () => Service().ImportAsync(encryptedPem, SourceSystemType.Epic, CancellationToken.None);

        await act.Should().ThrowAsync<BusinessRuleException>();
    }

    [Fact]
    public async Task ImportAsync_gives_every_call_its_own_unique_secret_name()
    {
        _secretWriter
            .Setup(x => x.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        using var rsaA = RSA.Create(2048);
        using var rsaB = RSA.Create(2048);

        var first = await Service().ImportAsync(rsaA.ExportPkcs8PrivateKeyPem(), SourceSystemType.Epic, CancellationToken.None);
        var second = await Service().ImportAsync(rsaB.ExportPkcs8PrivateKeyPem(), SourceSystemType.Epic, CancellationToken.None);

        first.SecretName.Should().NotBe(second.SecretName);
        first.KeyId.Should().NotBe(second.KeyId);
    }
}
