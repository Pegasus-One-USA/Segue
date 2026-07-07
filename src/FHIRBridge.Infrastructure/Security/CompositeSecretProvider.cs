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
    private readonly ILogger<CompositeSecretProvider> _logger;

    public CompositeSecretProvider(
        IConfiguration configuration,
        ConfigurationSecretProvider configurationSecretProvider,
        AzureKeyVaultSecretProvider azureKeyVaultSecretProvider,
        DbSecretStore dbSecretStore,
        ILogger<CompositeSecretProvider> logger)
    {
        _configuration = configuration;
        _configurationSecretProvider = configurationSecretProvider;
        _azureKeyVaultSecretProvider = azureKeyVaultSecretProvider;
        _dbSecretStore = dbSecretStore;
        _logger = logger;
    }

    public async Task<string> GetSecretAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var useAzureKeyVault = _configuration.GetValue("KeyVault:UseAzureKeyVault", false);
        var allowLocalFallback = _configuration.GetValue("KeyVault:AllowConfigurationFallback", true);

        if (!useAzureKeyVault)
        {
            return await ResolveLocalAsync(secretReference, cancellationToken);
        }

        try
        {
            return await _azureKeyVaultSecretProvider.GetSecretAsync(secretReference, cancellationToken);
        }
        catch (Exception exception) when (allowLocalFallback)
        {
            _logger.LogWarning(
                exception,
                "Azure Key Vault lookup failed for secret {SecretName} in vault {KeyVaultName}. Trying local fallback.",
                secretReference.SecretName,
                secretReference.KeyVaultName);

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
