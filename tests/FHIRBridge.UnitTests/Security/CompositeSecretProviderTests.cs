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
            new PassthroughTenantSecretVaultResolver(),
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
            new PassthroughTenantSecretVaultResolver(),
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
            new PassthroughTenantSecretVaultResolver(),
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
            new PassthroughTenantSecretVaultResolver(),
            NullLogger<CompositeSecretProvider>.Instance);

        var act = () => sut.GetSecretAsync(Reference, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Key_vault_mode_still_resolves_a_secret_stored_under_its_as_stored_vault_name()
    {
        // The state every secret provisioned before KeyVault:UseAzureKeyVault was switched on is in: the row is
        // keyed by the vault name that was persisted on the entity, not the configured vault the resolver now
        // rewrites every reference to.
        var dbSecretStore = CreateDbSecretStore();
        await dbSecretStore.WriteSecretAsync(Reference, "db-value", CancellationToken.None);
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("not found"));
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: true),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            dbSecretStore,
            new RewritingTenantSecretVaultResolver("configured-vault"),
            NullLogger<CompositeSecretProvider>.Instance);

        var value = await sut.GetSecretAsync(Reference, CancellationToken.None);

        value.Should().Be("db-value");
    }

    [Fact]
    public async Task Key_vault_is_tried_under_the_configured_vault_first_then_the_as_stored_vault()
    {
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.GetSecretAsync(
                new SecretReference("configured-vault", Reference.SecretName), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("not found"));
        keyVault
            .Setup(k => k.GetSecretAsync(Reference, It.IsAny<CancellationToken>()))
            .ReturnsAsync("operators-own-vault-value");
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: true),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            CreateDbSecretStore(),
            new RewritingTenantSecretVaultResolver("configured-vault"),
            NullLogger<CompositeSecretProvider>.Instance);

        var value = await sut.GetSecretAsync(Reference, CancellationToken.None);

        value.Should().Be("operators-own-vault-value");
        keyVault.Verify(
            k => k.GetSecretAsync(
                new SecretReference("configured-vault", Reference.SecretName), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Not_found_names_both_the_as_stored_and_the_configured_vault()
    {
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.GetSecretAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("not found"));
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: true),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            keyVault.Object,
            CreateDbSecretStore(),
            new RewritingTenantSecretVaultResolver("configured-vault"),
            NullLogger<CompositeSecretProvider>.Instance);

        var act = () => sut.GetSecretAsync(Reference, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<SecretNotConfiguredException>();
        thrown.Which.KeyVaultName.Should().Be("some-vault");
        thrown.Which.Message.Should().Contain("some-vault").And.Contain("configured-vault");
    }

    [Fact]
    public async Task Disabled_and_not_provisioned_locally_falls_through_to_configuration_and_throws_when_absent()
    {
        var sut = new CompositeSecretProvider(
            Configuration(useAzureKeyVault: false),
            new ConfigurationSecretProvider(new ConfigurationBuilder().Build()),
            KeyVaultMock().Object,
            CreateDbSecretStore(),
            new PassthroughTenantSecretVaultResolver(),
            NullLogger<CompositeSecretProvider>.Instance);

        var act = () => sut.GetSecretAsync(Reference, CancellationToken.None);

        await act.Should().ThrowAsync<SecretNotConfiguredException>();
    }
}

/// <summary>Stands in for KeyVaultAwareTenantSecretVaultResolver with Key Vault mode on: every requested vault
/// name is rewritten to the one configured tenant-secrets vault.</summary>
internal sealed class RewritingTenantSecretVaultResolver : FHIRBridge.Application.Abstractions.Security.ITenantSecretVaultResolver
{
    private readonly string _configuredVaultName;

    public RewritingTenantSecretVaultResolver(string configuredVaultName)
    {
        _configuredVaultName = configuredVaultName;
    }

    public string ResolveVaultName(string requestedKeyVaultName) => _configuredVaultName;
}
