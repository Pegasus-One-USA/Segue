using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Metadata-side twin of <see cref="CompositeSecretProvider"/>/<see cref="CompositeSecretWriter"/>: reports whether
/// a secret is provisioned by looking in the same place those two read and write it — Azure Key Vault when enabled,
/// falling back to the local DataProtection-encrypted <see cref="DbSecretStore"/> when it is disabled or the vault
/// lookup fails and fallback is allowed.
///
/// Without this, <see cref="IAppSecretMetadataProvider"/> bound straight to <see cref="DbSecretStore"/> queried only
/// the ProvisionedSecrets table while <see cref="CompositeSecretWriter"/> sent the value to Key Vault, so with
/// KeyVault:UseAzureKeyVault on, every "is it configured?" indicator (Settings → General → Terminology's LOINC
/// username/password and the SNOMED CT/RxNorm shared UTS API key, plus the NDC and notification settings screens)
/// reported "not set" for a credential that had saved correctly — and the terminology table's Run Now precheck
/// refused to start a sync that would in fact have resolved its credential fine.
/// </summary>
public sealed class CompositeSecretMetadataProvider : IAppSecretMetadataProvider
{
    private readonly IConfiguration _configuration;
    private readonly AzureKeyVaultSecretProvider _azureKeyVaultSecretProvider;
    private readonly DbSecretStore _dbSecretStore;
    private readonly ITenantSecretVaultResolver _vaultResolver;
    private readonly ILogger<CompositeSecretMetadataProvider> _logger;

    public CompositeSecretMetadataProvider(
        IConfiguration configuration,
        AzureKeyVaultSecretProvider azureKeyVaultSecretProvider,
        DbSecretStore dbSecretStore,
        ITenantSecretVaultResolver vaultResolver,
        ILogger<CompositeSecretMetadataProvider> logger)
    {
        _configuration = configuration;
        _azureKeyVaultSecretProvider = azureKeyVaultSecretProvider;
        _dbSecretStore = dbSecretStore;
        _vaultResolver = vaultResolver;
        _logger = logger;
    }

    public async Task<ProvisionedSecretMetadata> GetMetadataAsync(
        SecretReference secretReference,
        CancellationToken cancellationToken)
    {
        var useAzureKeyVault = _configuration.GetValue("KeyVault:UseAzureKeyVault", false);
        var allowLocalFallback = _configuration.GetValue("KeyVault:AllowConfigurationFallback", true);

        var requestedReference = secretReference;

        // Same resolve as CompositeSecretProvider/CompositeSecretWriter, for the same reason: app-level secrets
        // (terminology credentials, JWT signing key) pass a hardcoded "app" placeholder vault name that only
        // becomes the real configured vault here. Idempotent, so an already-resolved name is unaffected.
        secretReference = new SecretReference(
            _vaultResolver.ResolveVaultName(secretReference.KeyVaultName), secretReference.SecretName);

        if (!useAzureKeyVault)
        {
            return await _dbSecretStore.GetMetadataAsync(secretReference, cancellationToken);
        }

        // Prefix applied only to the reference actually sent to Key Vault, never to the local fallback below —
        // see CompositeSecretProvider's identical treatment for why the local lookup stays unprefixed.
        var keyVaultReference = SecretPrefixing.Apply(secretReference, _configuration["KeyVault:SecretPrefix"]);

        try
        {
            var metadata = await _azureKeyVaultSecretProvider.GetMetadataAsync(keyVaultReference, cancellationToken);
            if (metadata.Provisioned)
            {
                return metadata;
            }
        }
        catch (Exception exception) when (allowLocalFallback)
        {
            _logger.LogWarning(
                exception,
                "Azure Key Vault metadata lookup failed for secret {SecretName} in vault {KeyVaultName}. Trying local fallback.",
                keyVaultReference.SecretName,
                keyVaultReference.KeyVaultName);
        }

        // Reached when the vault has no such secret, or the lookup failed with fallback allowed. Both still check
        // locally: CompositeSecretWriter falls back to DbSecretStore on a failed vault write, so a credential saved
        // during a vault outage lives there, and secrets provisioned before Key Vault was switched on never moved.
        var localMetadata = await _dbSecretStore.GetMetadataAsync(secretReference, cancellationToken);
        if (localMetadata.Provisioned || requestedReference.KeyVaultName == secretReference.KeyVaultName)
        {
            return localMetadata;
        }

        // Finally, the pre-resolve name. A secret provisioned while Key Vault was off was stored under the raw
        // caller-supplied vault name (the "app" placeholder, for every app-level secret), so turning Key Vault on
        // would otherwise make an already-working credential read as unset purely because the lookup name moved.
        return await _dbSecretStore.GetMetadataAsync(requestedReference, cancellationToken);
    }
}
