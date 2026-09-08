using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

// Not sealed, and WriteSecretAsync is virtual, purely so CompositeSecretWriter's unit tests can mock this
// collaborator (Moq needs a non-sealed class with a virtual member to proxy) — no behavioral change.
public class AzureKeyVaultSecretWriter : ISecretWriter
{
    private readonly ILogger<AzureKeyVaultSecretWriter> _logger;
    private readonly Dictionary<string, SecretClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public AzureKeyVaultSecretWriter(ILogger<AzureKeyVaultSecretWriter> logger)
    {
        _logger = logger;
    }

    public virtual async Task WriteSecretAsync(
        SecretReference secretReference,
        string secretValue,
        CancellationToken cancellationToken)
    {
        var client = GetClient(secretReference.KeyVaultName);
        await client.SetSecretAsync(secretReference.SecretName, secretValue, cancellationToken);

        _logger.LogDebug(
            "Wrote secret {SecretName} to Azure Key Vault {KeyVaultName}.",
            secretReference.SecretName,
            secretReference.KeyVaultName);
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
