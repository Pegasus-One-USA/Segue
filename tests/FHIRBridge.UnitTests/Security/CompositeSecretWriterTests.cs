using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Security;

public sealed class CompositeSecretWriterTests
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

    private static Mock<AzureKeyVaultSecretWriter> KeyVaultMock() =>
        new(NullLogger<AzureKeyVaultSecretWriter>.Instance);

    [Fact]
    public async Task Disabled_writes_locally_without_touching_key_vault()
    {
        var dbSecretStore = CreateDbSecretStore();
        var keyVault = KeyVaultMock();
        var sut = new CompositeSecretWriter(
            Configuration(useAzureKeyVault: false),
            keyVault.Object,
            dbSecretStore,
            new PassthroughTenantSecretVaultResolver(),
            NullLogger<CompositeSecretWriter>.Instance);

        await sut.WriteSecretAsync(Reference, "local-value", CancellationToken.None);

        keyVault.Verify(
            k => k.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        (await dbSecretStore.TryGetSecretAsync(Reference, CancellationToken.None)).Should().Be("local-value");
    }

    [Fact]
    public async Task Enabled_and_key_vault_succeeds_never_touches_the_local_store()
    {
        var dbSecretStore = CreateDbSecretStore();
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.WriteSecretAsync(Reference, "kv-value", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var sut = new CompositeSecretWriter(
            Configuration(useAzureKeyVault: true),
            keyVault.Object,
            dbSecretStore,
            new PassthroughTenantSecretVaultResolver(),
            NullLogger<CompositeSecretWriter>.Instance);

        await sut.WriteSecretAsync(Reference, "kv-value", CancellationToken.None);

        keyVault.Verify(k => k.WriteSecretAsync(Reference, "kv-value", It.IsAny<CancellationToken>()), Times.Once);
        (await dbSecretStore.TryGetSecretAsync(Reference, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Enabled_and_key_vault_fails_with_fallback_allowed_writes_locally_instead()
    {
        var dbSecretStore = CreateDbSecretStore();
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vault unreachable"));
        var sut = new CompositeSecretWriter(
            Configuration(useAzureKeyVault: true, allowFallback: true),
            keyVault.Object,
            dbSecretStore,
            new PassthroughTenantSecretVaultResolver(),
            NullLogger<CompositeSecretWriter>.Instance);

        await sut.WriteSecretAsync(Reference, "fallback-value", CancellationToken.None);

        (await dbSecretStore.TryGetSecretAsync(Reference, CancellationToken.None)).Should().Be("fallback-value");
    }

    [Fact]
    public async Task Enabled_and_key_vault_fails_with_fallback_disallowed_propagates_and_writes_nothing_locally()
    {
        var dbSecretStore = CreateDbSecretStore();
        var keyVault = KeyVaultMock();
        keyVault
            .Setup(k => k.WriteSecretAsync(It.IsAny<SecretReference>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("vault unreachable"));
        var sut = new CompositeSecretWriter(
            Configuration(useAzureKeyVault: true, allowFallback: false),
            keyVault.Object,
            dbSecretStore,
            new PassthroughTenantSecretVaultResolver(),
            NullLogger<CompositeSecretWriter>.Instance);

        var act = () => sut.WriteSecretAsync(Reference, "never-persisted", CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await dbSecretStore.TryGetSecretAsync(Reference, CancellationToken.None)).Should().BeNull();
    }
}
