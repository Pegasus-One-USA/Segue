using Azure;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.Infrastructure.Persistence;
using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FHIRBridge.UnitTests.Security;

/// <summary>
/// Covers the split that made Settings -> General -> Terminology report LOINC's username/password and the
/// SNOMED CT/RxNorm shared UTS API key as unset right after they saved: with KeyVault:UseAzureKeyVault on,
/// CompositeSecretWriter sends the value to the vault, so a metadata provider that only queried the local
/// ProvisionedSecrets table always answered "not provisioned".
/// </summary>
public sealed class CompositeSecretMetadataProviderTests
{
    // static, shared root - see EfGovernanceQueryServiceSearchErrorLogsTests for why.
    private static readonly InMemoryDatabaseRoot _root = new();
    private readonly string _databaseName = Guid.NewGuid().ToString();

    private static readonly SecretReference LoincUsername = new("app", "loinc-basic-username");

    private FHIRBridgeDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FHIRBridgeDbContext>()
            .UseInMemoryDatabase(_databaseName, _root)
            .Options);

    private static IConfiguration Config(bool useKeyVault, bool allowFallback = true, string? prefix = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVault:UseAzureKeyVault"] = useKeyVault.ToString(),
            ["KeyVault:AllowConfigurationFallback"] = allowFallback.ToString(),
            ["KeyVault:VaultName"] = "fhirbridge-kv-test01",
            ["KeyVault:SecretPrefix"] = prefix,
        }).Build();

    private static DbSecretStore CreateDbStore(FHIRBridgeDbContext context) =>
        new(context, new EphemeralDataProtectionProvider(), NullLogger<DbSecretStore>.Instance);

    private static CompositeSecretMetadataProvider CreateSut(
        IConfiguration configuration, AzureKeyVaultSecretProvider vault, DbSecretStore dbStore) =>
        new(
            configuration,
            vault,
            dbStore,
            new KeyVaultAwareTenantSecretVaultResolver(
                configuration, NullLogger<KeyVaultAwareTenantSecretVaultResolver>.Instance),
            NullLogger<CompositeSecretMetadataProvider>.Instance);

    private static Mock<AzureKeyVaultSecretProvider> VaultReturning(ProvisionedSecretMetadata metadata)
    {
        var mock = new Mock<AzureKeyVaultSecretProvider>(NullLogger<AzureKeyVaultSecretProvider>.Instance);
        mock.Setup(v => v.GetMetadataAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(metadata);
        return mock;
    }

    private static Mock<AzureKeyVaultSecretProvider> VaultThrowing(Exception exception)
    {
        var mock = new Mock<AzureKeyVaultSecretProvider>(NullLogger<AzureKeyVaultSecretProvider>.Instance);
        mock.Setup(v => v.GetMetadataAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(exception);
        return mock;
    }

    [Fact]
    public async Task Reports_provisioned_for_a_secret_that_lives_only_in_key_vault()
    {
        // The reported bug: saved through CompositeSecretWriter into the vault, so nothing is in
        // ProvisionedSecrets, yet the Run Now precheck must still see the credential as set.
        await using var context = CreateContext();
        var rotatedOn = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        var vault = VaultReturning(new ProvisionedSecretMetadata(true, rotatedOn));
        var sut = CreateSut(Config(useKeyVault: true), vault.Object, CreateDbStore(context));

        var metadata = await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        metadata.Provisioned.Should().BeTrue();
        metadata.LastRotatedUtc.Should().Be(rotatedOn);
    }

    [Fact]
    public async Task Resolves_the_app_placeholder_to_the_configured_vault_before_looking_it_up()
    {
        // Terminology credentials hardcode the "app" placeholder vault name; only the resolver turns it into
        // the real one, so a lookup that skipped the resolve would query a vault that does not exist.
        await using var context = CreateContext();
        var vault = VaultReturning(new ProvisionedSecretMetadata(true, null));
        var sut = CreateSut(Config(useKeyVault: true), vault.Object, CreateDbStore(context));

        await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        vault.Verify(
            v => v.GetMetadataAsync(
                It.Is<SecretReference>(r => r.KeyVaultName == "fhirbridge-kv-test01"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Applies_the_secret_prefix_to_the_key_vault_lookup_only()
    {
        await using var context = CreateContext();
        var vault = VaultReturning(new ProvisionedSecretMetadata(true, null));
        var sut = CreateSut(Config(useKeyVault: true, prefix: "dev"), vault.Object, CreateDbStore(context));

        await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        vault.Verify(
            v => v.GetMetadataAsync(
                It.Is<SecretReference>(r => r.SecretName == "dev--loinc-basic-username"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task Falls_back_to_the_local_store_when_the_vault_has_no_such_secret()
    {
        // Secrets provisioned before Key Vault was switched on never moved into the vault.
        await using var context = CreateContext();
        var dbStore = CreateDbStore(context);
        await dbStore.WriteSecretAsync(LoincUsername, "legacy-user", CancellationToken.None);
        var vault = VaultReturning(new ProvisionedSecretMetadata(false, null));
        var sut = CreateSut(Config(useKeyVault: true), vault.Object, dbStore);

        var metadata = await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        metadata.Provisioned.Should().BeTrue();
    }

    [Fact]
    public async Task Falls_back_to_the_local_store_when_the_vault_lookup_fails()
    {
        // Mirrors the failed-write fallback in CompositeSecretWriter: a credential saved during a vault
        // outage lands in ProvisionedSecrets, and must still read back as provisioned.
        await using var context = CreateContext();
        var dbStore = CreateDbStore(context);
        await dbStore.WriteSecretAsync(LoincUsername, "fallback-user", CancellationToken.None);
        var vault = VaultThrowing(new RequestFailedException(403, "Forbidden"));
        var sut = CreateSut(Config(useKeyVault: true), vault.Object, dbStore);

        var metadata = await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        metadata.Provisioned.Should().BeTrue();
    }

    [Fact]
    public async Task Propagates_a_vault_failure_when_local_fallback_is_disabled()
    {
        // With fallback off there is no second place to look, so "vault unreachable" must not quietly
        // render as "credential not configured".
        await using var context = CreateContext();
        var vault = VaultThrowing(new RequestFailedException(403, "Forbidden"));
        var sut = CreateSut(Config(useKeyVault: true, allowFallback: false), vault.Object, CreateDbStore(context));

        await sut.Invoking(s => s.GetMetadataAsync(LoincUsername, CancellationToken.None))
            .Should().ThrowAsync<RequestFailedException>();
    }

    [Fact]
    public async Task Uses_only_the_local_store_when_key_vault_is_disabled()
    {
        await using var context = CreateContext();
        var dbStore = CreateDbStore(context);
        await dbStore.WriteSecretAsync(LoincUsername, "local-user", CancellationToken.None);
        var vault = new Mock<AzureKeyVaultSecretProvider>(NullLogger<AzureKeyVaultSecretProvider>.Instance);
        var sut = CreateSut(Config(useKeyVault: false), vault.Object, dbStore);

        var metadata = await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        metadata.Provisioned.Should().BeTrue();
        vault.Verify(
            v => v.GetMetadataAsync(It.IsAny<SecretReference>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Reports_unprovisioned_when_neither_store_has_the_secret()
    {
        await using var context = CreateContext();
        var vault = VaultReturning(new ProvisionedSecretMetadata(false, null));
        var sut = CreateSut(Config(useKeyVault: true), vault.Object, CreateDbStore(context));

        var metadata = await sut.GetMetadataAsync(LoincUsername, CancellationToken.None);

        metadata.Provisioned.Should().BeFalse();
        metadata.LastRotatedUtc.Should().BeNull();
    }
}
