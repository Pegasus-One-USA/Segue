using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

// Not sealed, and GetSecretAsync is virtual, purely so CompositeSecretProvider's unit tests can mock this
// collaborator (Moq needs a non-sealed class with a virtual member to proxy) — no behavioral change.
public class AzureKeyVaultSecretProvider : ISecretProvider
{
    private readonly ILogger<AzureKeyVaultSecretProvider> _logger;
    private readonly Dictionary<string, SecretClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public AzureKeyVaultSecretProvider(ILogger<AzureKeyVaultSecretProvider> logger)
    {
        _logger = logger;
    }

    public virtual async Task<string> GetSecretAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var client = GetClient(secretReference.KeyVaultName);
        var secret = await client.GetSecretAsync(secretReference.SecretName, cancellationToken: cancellationToken);

        if (string.IsNullOrWhiteSpace(secret.Value.Value))
        {
            // Deliberately NOT InvalidOperationException: that gets message-substring-classified as a 4xx by
            // Program.cs's MapException and skips ErrorLogs as a "routine" outcome — but an empty vault secret is
            // a genuine deployment/configuration fault, the same class of bug ConfigurationSecretProvider had.
            throw new SecretNotConfiguredException(
                secretReference.SecretName,
                secretReference.KeyVaultName,
                $"Secret '{secretReference.SecretName}' in vault '{secretReference.KeyVaultName}' is empty.");
        }

        _logger.LogDebug(
            "Resolved secret {SecretName} from Azure Key Vault {KeyVaultName}.",
            secretReference.SecretName,
            secretReference.KeyVaultName);

        return secret.Value.Value;
    }

    /// <summary>Whether a secret exists in the vault and when it was last updated, without ever returning
    /// the value. Uses GetSecretAsync (not GetPropertiesOfSecret) so an existing-but-empty secret reports
    /// unprovisioned, matching <see cref="GetSecretAsync"/>'s own empty-value-is-a-fault rule; a missing
    /// secret is a normal "not set yet" answer, so RequestFailedException/404 returns false rather than
    /// throwing. Any other failure (auth, network) propagates — callers must not read "vault unreachable"
    /// as "credential not configured".</summary>
    public virtual async Task<ProvisionedSecretMetadata> GetMetadataAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var client = GetClient(secretReference.KeyVaultName);

        try
        {
            var secret = await client.GetSecretAsync(secretReference.SecretName, cancellationToken: cancellationToken);
            return string.IsNullOrWhiteSpace(secret.Value.Value)
                ? new ProvisionedSecretMetadata(false, null)
                : new ProvisionedSecretMetadata(true, secret.Value.Properties.UpdatedOn?.UtcDateTime);
        }
        catch (RequestFailedException exception) when (exception.Status == 404)
        {
            return new ProvisionedSecretMetadata(false, null);
        }
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
