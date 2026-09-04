using FHIRBridge.Application.Abstractions.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

public sealed class KeyVaultAwareTenantSecretVaultResolver : ITenantSecretVaultResolver
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<KeyVaultAwareTenantSecretVaultResolver> _logger;

    public KeyVaultAwareTenantSecretVaultResolver(
        IConfiguration configuration,
        ILogger<KeyVaultAwareTenantSecretVaultResolver> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string ResolveVaultName(string requestedKeyVaultName)
    {
        var useAzureKeyVault = _configuration.GetValue("KeyVault:UseAzureKeyVault", false);
        if (!useAzureKeyVault)
        {
            return requestedKeyVaultName;
        }

        var configuredVaultName = _configuration["KeyVault:VaultName"];
        if (string.IsNullOrWhiteSpace(configuredVaultName))
        {
            _logger.LogWarning(
                "KeyVault:UseAzureKeyVault is true but KeyVault:VaultName is not configured. Using the " +
                "caller-supplied '{RequestedKeyVaultName}' as-is, which is almost certainly not a real Azure Key " +
                "Vault name.",
                requestedKeyVaultName);
            return requestedKeyVaultName;
        }

        return configuredVaultName;
    }
}
