using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// The one place the <c>KeyVault:SecretPrefix</c> "&lt;prefix&gt;--" convention is actually implemented —
/// every other class that needs to apply, detect, or strip it (<see cref="CompositeSecretProvider"/>,
/// <see cref="CompositeSecretWriter"/>, <see cref="KeyVaultConfigurationExtensions"/>'s secret manager) calls
/// into this rather than re-implementing the separator/format itself, so the three can't drift out of sync.
/// Distinguishes environments/developers sharing one vault (e.g. Dev/QA/Staging/Working, or several
/// developers' local test vaults) — applied only to the name actually sent to/read from Key Vault, never
/// persisted onto DestinationConfiguration/SourceConnection/ProvisionedSecret rows.
/// </summary>
public static class SecretPrefixing
{
    private const string Separator = "--";

    /// <summary>Returns <paramref name="secretReference"/> unchanged when <paramref name="prefix"/> is
    /// blank; otherwise a copy with the prefix prepended to the secret name (never the vault name).</summary>
    public static SecretReference Apply(SecretReference secretReference, string? prefix) =>
        string.IsNullOrWhiteSpace(prefix)
            ? secretReference
            : new SecretReference(secretReference.KeyVaultName, $"{prefix}{Separator}{secretReference.SecretName}");

    /// <summary>True when <paramref name="secretName"/> belongs to <paramref name="prefix"/> — always true
    /// when <paramref name="prefix"/> is blank (nothing to filter by).</summary>
    public static bool BelongsToPrefix(string secretName, string? prefix) =>
        string.IsNullOrWhiteSpace(prefix) ||
        secretName.StartsWith(prefix + Separator, StringComparison.OrdinalIgnoreCase);

    /// <summary>Removes the "&lt;prefix&gt;--" lead-in, if any — returns <paramref name="secretName"/>
    /// unchanged when <paramref name="prefix"/> is blank.</summary>
    public static string Strip(string secretName, string? prefix) =>
        string.IsNullOrWhiteSpace(prefix) ? secretName : secretName[(prefix.Length + Separator.Length)..];
}
