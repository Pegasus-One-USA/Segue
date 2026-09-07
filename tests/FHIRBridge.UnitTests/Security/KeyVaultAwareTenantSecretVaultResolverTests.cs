using FHIRBridge.Infrastructure.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace FHIRBridge.UnitTests.Security;

public sealed class KeyVaultAwareTenantSecretVaultResolverTests
{
    private static KeyVaultAwareTenantSecretVaultResolver Resolver(Dictionary<string, string?> settings) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(settings).Build(),
            NullLogger<KeyVaultAwareTenantSecretVaultResolver>.Instance);

    [Fact]
    public void Key_vault_disabled_returns_the_requested_name_unchanged()
    {
        var resolver = Resolver(new() { ["KeyVault:UseAzureKeyVault"] = "false" });

        resolver.ResolveVaultName("workflow-secrets").Should().Be("workflow-secrets");
    }

    [Fact]
    public void Key_vault_enabled_with_a_configured_vault_name_overrides_the_requested_name()
    {
        var resolver = Resolver(new()
        {
            ["KeyVault:UseAzureKeyVault"] = "true",
            ["KeyVault:VaultName"] = "seguedev",
        });

        resolver.ResolveVaultName("workflow-secrets").Should().Be("seguedev");
    }

    [Fact]
    public void Key_vault_enabled_without_a_configured_vault_name_falls_back_to_the_requested_name()
    {
        var resolver = Resolver(new() { ["KeyVault:UseAzureKeyVault"] = "true" });

        resolver.ResolveVaultName("workflow-secrets").Should().Be("workflow-secrets");
    }
}
