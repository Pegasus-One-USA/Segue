using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;
using FHIRBridge.SharedKernel.Exceptions;
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

        var candidates = BuildCandidates(secretReference);

        if (!useAzureKeyVault)
        {
            return await ResolveLocalAsync(candidates, cancellationToken);
        }

        // KeyVault:SecretPrefix (see SecretPrefixing) distinguishes environments/developers sharing one vault
        // (e.g. Dev/QA/Staging/Working all pointed at the same vault, or several developers' local test
        // vaults) — applied ONLY to the reference actually sent to Key Vault, never persisted anywhere and
        // never applied to the local fallback below: each environment already has its own separate database,
        // so there's no local collision to prevent, and prefixing that lookup too would make an
        // already-stored local secret suddenly unfindable under its old, unprefixed key.
        var prefix = _configuration["KeyVault:SecretPrefix"];

        for (var i = 0; i < candidates.Count; i++)
        {
            var keyVaultReference = SecretPrefixing.Apply(candidates[i], prefix);

            try
            {
                return await _azureKeyVaultSecretProvider.GetSecretAsync(keyVaultReference, cancellationToken);
            }
            // A miss on a non-final candidate is never fatal — there's still another vault to look in, whatever
            // KeyVault:AllowConfigurationFallback says (that flag governs falling back to LOCAL storage, not
            // whether the as-stored vault gets tried at all).
            catch (Exception exception) when (allowLocalFallback || i < candidates.Count - 1)
            {
                _logger.LogWarning(
                    exception,
                    "Azure Key Vault lookup failed for secret {SecretName} in vault {KeyVaultName}. Trying the next candidate.",
                    keyVaultReference.SecretName,
                    keyVaultReference.KeyVaultName);
            }
        }

        return await ResolveLocalAsync(candidates, cancellationToken);
    }

    /// <summary>
    /// The references to look the secret up under, most-likely first: the vault name as resolved by
    /// <see cref="ITenantSecretVaultResolver"/> (the one configured tenant-secrets vault every app-provisioned
    /// secret is written to when Key Vault mode is on), then — when the resolver actually rewrote it — the
    /// reference exactly as persisted on the entity.
    /// <para>
    /// That second candidate matters in two real cases the resolved-name-only lookup silently broke: a secret
    /// provisioned BEFORE Key Vault mode was switched on (stored locally under its original vault name, e.g.
    /// "workflow-secrets" or "signing-keys", and instantly unreadable the moment the rewrite kicked in), and a
    /// hand-entered reference to a vault the operator genuinely owns, whose whole point is naming a specific
    /// vault — silently redirecting that one to the configured vault makes the reference unresolvable by
    /// construction. Reads have to be the tolerant side of this: writes still go to exactly one place
    /// (see <see cref="CompositeSecretWriter"/>), so nothing here creates a second copy or an ambiguous winner.
    /// </para>
    /// </summary>
    private IReadOnlyList<SecretReference> BuildCandidates(SecretReference secretReference)
    {
        var resolvedVaultName = _vaultResolver.ResolveVaultName(secretReference.KeyVaultName);

        if (string.Equals(resolvedVaultName, secretReference.KeyVaultName, StringComparison.OrdinalIgnoreCase))
        {
            return new[] { secretReference };
        }

        return new[]
        {
            new SecretReference(resolvedVaultName, secretReference.SecretName),
            secretReference,
        };
    }

    // App-provisioned (B1) secrets take precedence over configuration/env secrets so a wizard-provisioned connection
    // string resolves without any config change; falls back to configuration for operator-provided secrets. Each
    // tier is exhausted across every candidate reference before dropping to the next, so a locally provisioned
    // value always beats a same-named configuration entry regardless of which vault name it was stored under.
    private async Task<string> ResolveLocalAsync(
        IReadOnlyList<SecretReference> candidates, CancellationToken cancellationToken)
    {
        foreach (var candidate in candidates)
        {
            var provisioned = await _dbSecretStore.TryGetSecretAsync(candidate, cancellationToken);
            if (provisioned is not null)
            {
                return provisioned;
            }
        }

        foreach (var candidate in candidates)
        {
            var configured = _configurationSecretProvider.TryGetSecret(candidate);
            if (configured is not null)
            {
                return configured;
            }
        }

        throw NotFound(candidates);
    }

    /// <summary>
    /// Names every vault actually searched. The single-candidate message is the historical wording; the
    /// two-candidate one has to name both, because reporting only the resolved vault reads as a nonsense error to
    /// an operator whose connection shows the other name on screen.
    /// </summary>
    private static SecretNotConfiguredException NotFound(IReadOnlyList<SecretReference> candidates)
    {
        // The as-stored reference (always last) is the one the operator configured, so it owns the exception's
        // KeyVaultName property.
        var stored = candidates[^1];

        if (candidates.Count == 1)
        {
            return new SecretNotConfiguredException(stored.SecretName, stored.KeyVaultName);
        }

        return new SecretNotConfiguredException(
            stored.SecretName,
            stored.KeyVaultName,
            $"Secret '{stored.SecretName}' was not found in vault '{stored.KeyVaultName}' or in the configured " +
            $"Key Vault '{candidates[0].KeyVaultName}'.");
    }
}
