using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.Extensions.Configuration;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Wires Azure Key Vault as a plain <see cref="IConfiguration"/> source, for the handful of secrets that live
/// outside the tenant/app secret system (<see cref="ISecretProvider"/>/<see cref="ISecretWriter"/>) because
/// they're needed before that system can even run — most notably <c>ConnectionStrings:FHIRBridgeDb</c> itself
/// (the database <see cref="DbSecretStore"/> depends on), plus <c>ConnectionStrings:Redis</c> and the
/// messaging broker connection strings (<c>Messaging:AzureServiceBus:ConnectionString</c>,
/// <c>Messaging:RabbitMq:Password</c>). Everything else credential-shaped (SourceConnection/
/// DestinationConfiguration secrets, the JWT signing key, SAML's signing key, SMTP's password, terminology
/// credentials) already flows through that system and needs no separate wiring here.
///
/// Key Vault secret names can't contain ':', so the Azure config provider maps "--" to the config path
/// separator instead — e.g. a secret named <c>ConnectionStrings--FHIRBridgeDb</c> becomes config key
/// <c>ConnectionStrings:FHIRBridgeDb</c>, and its value simply overrides whatever appsettings/env-var value
/// was there before (Key Vault is added last, after every other configuration source has already loaded).
///
/// When <c>KeyVault:SecretPrefix</c> is set — same key <see cref="CompositeSecretProvider"/>/
/// <see cref="CompositeSecretWriter"/> use, for environments/developers sharing one vault — only secrets
/// named <c>&lt;prefix&gt;--...</c> are loaded at all, and that prefix is stripped before the "--"-to-":"
/// mapping runs, so a Dev host only ever sees its own <c>Dev--ConnectionStrings--FHIRBridgeDb</c> secret as
/// plain config key <c>ConnectionStrings:FHIRBridgeDb</c> — a QA/Staging/Working secret in the same vault is
/// invisible to it entirely, not just unused.
/// </summary>
public static class KeyVaultConfigurationExtensions
{
    /// <summary>Call once, early in Program.cs — before anything reads ConnectionStrings/Messaging config —
    /// right after the host builder is created. Gated by the same <c>KeyVault:UseAzureKeyVault</c>/
    /// <c>KeyVault:VaultName</c> keys the tenant/app secret system uses, so one flag turns both on together.
    /// Deliberately graceful on failure: <see cref="IConfigurationManager"/>'s <c>Add</c> triggers an
    /// immediate, synchronous load (unlike a plain <see cref="IConfigurationBuilder"/>), so an unreachable
    /// vault at boot must not take the whole host down — it boots on whatever's already in appsettings/env
    /// vars for these same keys instead, matching <c>KeyVault:AllowConfigurationFallback</c>'s spirit
    /// elsewhere. There is no logger yet this early in the host's lifecycle, so failures go to stderr — still
    /// captured by whatever's hosting the process (Windows Event Log via the service wrapper, container
    /// stdout, etc.).</summary>
    public static void AddFhirBridgeKeyVaultConfiguration(this IConfigurationManager configuration)
    {
        if (!configuration.GetValue("KeyVault:UseAzureKeyVault", false))
        {
            return;
        }

        var vaultName = configuration["KeyVault:VaultName"];
        if (string.IsNullOrWhiteSpace(vaultName))
        {
            return;
        }

        try
        {
            var vaultUri = vaultName.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                ? new Uri(vaultName)
                : new Uri($"https://{vaultName}.vault.azure.net/");
            var secretPrefix = configuration["KeyVault:SecretPrefix"];
            configuration.AddAzureKeyVault(
                vaultUri,
                new DefaultAzureCredential(),
                new PrefixedKeyVaultSecretManager(secretPrefix));
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"WARNING: Failed to load configuration from Azure Key Vault '{vaultName}' — continuing with " +
                $"local configuration (appsettings/env vars) only. {exception}");
        }
    }

    /// <summary>Only loads (and only maps to config keys) secrets belonging to the configured
    /// KeyVault:SecretPrefix — every other secret in a shared vault is invisible to this host, not merely
    /// unused. Load/GetKey delegate to <see cref="SecretPrefixing"/> — the one place this convention is
    /// actually implemented — rather than re-deriving the separator/format here.</summary>
    private sealed class PrefixedKeyVaultSecretManager : KeyVaultSecretManager
    {
        private readonly string? _prefix;

        public PrefixedKeyVaultSecretManager(string? prefix)
        {
            _prefix = prefix;
        }

        public override bool Load(SecretProperties secret) =>
            SecretPrefixing.BelongsToPrefix(secret.Name, _prefix);

        public override string GetKey(KeyVaultSecret secret) =>
            SecretPrefixing.Strip(secret.Name, _prefix)
                .Replace("--", ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);
    }
}
