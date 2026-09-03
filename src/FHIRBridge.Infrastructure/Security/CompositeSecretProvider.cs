using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

public sealed class CompositeSecretProvider : ISecretProvider
{
    private readonly IConfiguration _configuration;
    private readonly ConfigurationSecretProvider _configurationSecretProvider;
    private readonly AzureKeyVaultSecretProvider _azureKeyVaultSecretProvider;
    private readonly DbSecretStore _dbSecretStore;
    private readonly ITenantSecretVaultResolver _vaultResolver;
    private readonly ILogger<CompositeSecretProvider> _logger;

    public CompositeSecretProvider(
        IConfiguration configuration,
        ConfigurationSecretProvider configurationSecretProvider,
        AzureKeyVaultSecretProvider azureKeyVaultSecretProvider,
        DbSecretStore dbSecretStore,
        ITenantSecretVaultResolver vaultResolver,
        ILogger<CompositeSecretProvider> logger)
    {
        _configuration = configuration;
        _configurationSecretProvider = configurationSecretProvider;
        _azureKeyVaultSecretProvider = azureKeyVaultSecretProvider;
        _dbSecretStore = dbSecretStore;
        _vaultResolver = vaultResolver;
        _logger = logger;
    }

    public async Task<string> GetSecretAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var useAzureKeyVault = _configuration.GetValue("KeyVault:UseAzureKeyVault", false);
        var allowLocalFallback = _configuration.GetValue("KeyVault:AllowConfigurationFallback", true);

        // Resolved here (not left to each caller) so EVERY secret reference — tenant SourceConnection/
        // DestinationConfiguration secrets AND app-level secrets (JWT signing key, terminology credentials,
        // which historically passed a hardcoded "app" placeholder straight through, uncorrected) — reaches
        // the real configured vault when Key Vault mode is on. Idempotent: ConfigurationService.cs's own
        // explicit resolve-before-call sites still work fine, resolving an already-resolved name is a no-op.
        secretReference = new SecretReference(
            _vaultResolver.ResolveVaultName(secretReference.KeyVaultName), secretReference.SecretName);

        if (!useAzureKeyVault)
        {
            return await ResolveLocalAsync(secretReference, cancellationToken);
        }

        // KeyVault:SecretPrefix (see SecretPrefixing) distinguishes environments/developers sharing one vault
        // (e.g. Dev/QA/Staging/Working all pointed at the same vault, or several developers' local test
        // vaults) — applied ONLY to the reference actually sent to Key Vault, never persisted anywhere and
        // never applied to the local fallback below: each environment already has its own separate database,
        // so there's no local collision to prevent, and prefixing that lookup too would make an
        // already-stored local secret suddenly unfindable under its old, unprefixed key.
        var keyVaultReference = SecretPrefixing.Apply(secretReference, _configuration["KeyVault:SecretPrefix"]);

        try
        {
            return await _azureKeyVaultSecretProvider.GetSecretAsync(keyVaultReference, cancellationToken);
        }
        catch (Exception exception) when (allowLocalFallback)
        {
            _logger.LogWarning(
                exception,
                "Azure Key Vault lookup failed for secret {SecretName} in vault {KeyVaultName}. Trying local fallback.",
                keyVaultReference.SecretName,
                keyVaultReference.KeyVaultName);

            return await ResolveLocalAsync(secretReference, cancellationToken);
        }
    }

    // App-provisioned (B1) secrets take precedence over configuration/env secrets so a wizard-provisioned connection
    // string resolves without any config change; falls back to configuration for operator-provided secrets.
    private async Task<string> ResolveLocalAsync(SecretReference secretReference, CancellationToken cancellationToken)
    {
        var provisioned = await _dbSecretStore.TryGetSecretAsync(secretReference, cancellationToken);
        return provisioned ?? await _configurationSecretProvider.GetSecretAsync(secretReference, cancellationToken);
    }
}
