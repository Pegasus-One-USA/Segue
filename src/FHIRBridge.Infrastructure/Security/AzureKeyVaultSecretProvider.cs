using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

public sealed class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly ILogger<AzureKeyVaultSecretProvider> _logger;
    private readonly Dictionary<string, SecretClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public AzureKeyVaultSecretProvider(ILogger<AzureKeyVaultSecretProvider> logger)
    {
        _logger = logger;
    }

    public async Task<string> GetSecretAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var client = GetClient(secretReference.KeyVaultName);
        var secret = await client.GetSecretAsync(secretReference.SecretName, cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(secret.Value.Value))
        {
            throw new InvalidOperationException(
                $"Secret '{secretReference.SecretName}' in vault '{secretReference.KeyVaultName}' is empty.");
        }

        _logger.LogDebug(
            "Resolved secret {SecretName} from Azure Key Vault {KeyVaultName}.",
            secretReference.SecretName,
            secretReference.KeyVaultName);

        return secret.Value.Value;
    }

    private SecretClient GetClient(string keyVaultNameOrUri)
    {
        lock (_clients)
        {
            if (_clients.TryGetValue(keyVaultNameOrUri, out var existingClient))
            {
                return existingClient;
            }

            var vaultUri = keyVaultNameOrUri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(keyVaultNameOrUri)
                : new Uri($"https://{keyVaultNameOrUri}.vault.azure.net/");
            var client = new SecretClient(vaultUri, new DefaultAzureCredential());
            _clients[keyVaultNameOrUri] = client;

            return client;
        }
    }
}
