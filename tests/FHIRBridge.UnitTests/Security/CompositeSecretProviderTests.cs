using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Security;
using FHIRBridge.SharedKernel.Exceptions;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Security;

public sealed class CompositeSecretProviderTests
{
    private static readonly SecretReference Reference = new("some-vault", "some-secret");

    private static DbSecretStore CreateDbSecretStore()
    {
        var options = new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new DbSecretStore(
            new FHIRBridgeDbContext(options),
            new EphemeralDataProtectionProvider(),
            NullLogger<DbSecretStore>.Instance);
    }

    private static IConfiguration Configuration(bool useAzureKeyVault, bool allowFallback = true) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:UseAzureKeyVault"] = useAzureKeyVault.ToString(),
            ["KeyVault:AllowConfigurationFallback"] = allowFallback.ToString(),
        }).Build();

    private static Mock<AzureKeyVaultSecretProvider> KeyVaultMock() =>
        new(NullLogger<AzureKeyVaultSecretProvider>.Instance);

    [Fact]
    public async Task Disabled_resolves_locally_without_touching_key_vault()
    {
        var dbSecretStore = CreateDbSecretStore();
        await dbSecretStore.WriteSecretAsync(Reference, "db-value", CancellationToken.None);
        var keyVault = KeyVaultMock();
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: false),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            dbSecretStore,
            NullLogger<CompositeSecretProvider>.Instance);

        var value = await sut.GetSecretAsync(Reference, CancellationToken.None);

        value.Should().Be("db-value");
        keyVault.Verify(
            k => k.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Enabled_and_key_vault_succeeds_returns_the_key_vault_value()
    {
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.GetSecretAsync(Reference, It.IsAny<CancellationToken>()))
            .ReturnsAsync("kv-value");
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: true),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            CreateDbSecretStore(),
            NullLogger<CompositeSecretProvider>.Instance);

        var value = await sut.GetSecretAsync(Reference, CancellationToken.None);

        value.Should().Be("kv-value");
    }

    [Fact]
    public async Task Enabled_and_key_vault_fails_with_fallback_allowed_resolves_locally()
    {
        var dbSecretStore = CreateDbSecretStore();
        await dbSecretStore.WriteSecretAsync(Reference, "db-value", CancellationToken.None);
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.GetSecretAsync(Reference, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vault unreachable"));
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: true, allowFallback: true),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            dbSecretStore,
            NullLogger<CompositeSecretProvider>.Instance);

        var value = await sut.GetSecretAsync(Reference, CancellationToken.None);

        value.Should().Be("db-value");
    }

    [Fact]
    public async Task Enabled_and_key_vault_fails_with_fallback_disallowed_propagates()
    {
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.GetSecretAsync(Reference, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vault unreachable"));
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: true, allowFallback: false),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            CreateDbSecretStore(),
            NullLogger<CompositeSecretProvider>.Instance);

        var act = () => sut.GetSecretAsync(Reference, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Disabled_and_not_provisioned_locally_falls_through_to_configuration_and_throws_when_absent()
    {
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: false),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            KeyVaultMock().Object,
            CreateDbSecretStore(),
            NullLogger<CompositeSecretProvider>.Instance);

        var act = () => sut.GetSecretAsync(Reference, CancellationToken.None);

        await act.Should().ThrowAsync<SecretNotConfiguredException>();
    }
}
