using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Write-side twin of <see cref="CompositeSecretProvider"/>: provisions into Azure Key Vault when enabled, falling
/// back to the local DataProtection-encrypted <see cref="DbSecretStore"/> when Key Vault is disabled or a write to
/// it fails and fallback is allowed. Read/write take the same two config keys so a secret's storage location is
/// consistent between <see cref="ISecretWriter.WriteSecretAsync"/> and <see cref="ISecretProvider.GetSecretAsync"/>.
/// </summary>
public sealed class CompositeSecretWriter : ISecretWriter
{
    private readonly IConfiguration _configuration;
    private readonly AzureKeyVaultSecretWriter _azureKeyVaultSecretWriter;
    private readonly DbSecretStore _dbSecretStore;
    private readonly ITenantSecretVaultResolver _vaultResolver;
    private readonly ILogger<CompositeSecretWriter> _logger;

    public CompositeSecretWriter(
        IConfiguration configuration,
        AzureKeyVaultSecretWriter azureKeyVaultSecretWriter,
        DbSecretStore dbSecretStore,
        ITenantSecretVaultResolver vaultResolver,
        ILogger<CompositeSecretWriter> logger)
    {
        _configuration = configuration;
        _azureKeyVaultSecretWriter = azureKeyVaultSecretWriter;
        _dbSecretStore = dbSecretStore;
        _vaultResolver = vaultResolver;
        _logger = logger;
    }

    public async Task WriteSecretAsync(
        SecretReference secretReference,
        string secretValue,
        CancellationToken cancellationToken)
    {
        var useAzureKeyVault = _configuration.GetValue("KeyVault:UseAzureKeyVault", false);
        var allowLocalFallback = _configuration.GetValue("KeyVault:AllowConfigurationFallback", true);

        // See CompositeSecretProvider's identical resolve — fixes app-level secrets (JWT signing key,
        // terminology credentials) that previously passed a hardcoded "app" placeholder straight through
        // uncorrected, uniformly for every caller rather than each one having to remember to resolve first.
        secretReference = new SecretReference(
            _vaultResolver.ResolveVaultName(secretReference.KeyVaultName), secretReference.SecretName);

        if (!useAzureKeyVault)
        {
            await _dbSecretStore.WriteSecretAsync(secretReference, secretValue, cancellationToken);
            return;
        }

        // See CompositeSecretProvider's identical use of SecretPrefixing — applied only to the reference
        // actually sent to Key Vault, never persisted, never applied to the local fallback below.
        var keyVaultReference = SecretPrefixing.Apply(secretReference, _configuration["KeyVault:SecretPrefix"]);

        try
        {
            await _azureKeyVaultSecretWriter.WriteSecretAsync(keyVaultReference, secretValue, cancellationToken);
        }
        catch (Exception exception) when (allowLocalFallback)
        {
            _logger.LogWarning(
                exception,
                "Azure Key Vault write failed for secret {SecretName} in vault {KeyVaultName}. Falling back to local storage.",
                keyVaultReference.SecretName,
                keyVaultReference.KeyVaultName);

            await _dbSecretStore.WriteSecretAsync(secretReference, secretValue, cancellationToken);
        }
    }
}
