using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Sources;

/// <summary>
/// Shared validation for a SMART Backend Services signing key resolved from a secret reference. Every caller that
/// pulls the key out of <c>ISecretProvider</c> runs it through here before handing it to <c>RSA.ImportFromPem</c>,
/// so the failure names the connection and the exact (vault, secret) instead of the crypto layer's misleading
/// message. <c>BackendServicesJwtFactory</c> keeps its own backstop for callers handed a PEM they didn't resolve.
/// </summary>
internal static class SigningKeySecretGuard
{
    /// <summary>
    /// Fails a signing secret that resolved to something that plainly isn't a PEM private key, naming the exact
    /// (vault, secret) it came from. Without this the value flows on to <c>RSA.ImportFromPem</c>, which throws
    /// <c>ArgumentException: No supported key formats were found. Check that the input represents the contents of a
    /// PEM-encoded key file, not the path to such a file</c> — a message that names neither the connection nor the
    /// secret, and actively misleads by blaming a file path when the real cause is usually a secret store returning
    /// an unset placeholder (appsettings ships <c>Secrets:dev-local:epic-backend-private-key</c> as the literal
    /// "SET_VIA_DOTNET_USER_SECRETS") or a reference pointing at a slot that was never populated.
    /// </summary>
    internal static void EnsurePemShaped(string? secretValue, SecretReference reference, string? connectionName)
    {
        if (secretValue is not null && secretValue.AsSpan().TrimStart().StartsWith("-----BEGIN"))
        {
            return;
        }

        // Never include the value itself — it is key material whenever it IS valid, and this same message is
        // reached for a merely-truncated or wrongly-encoded real key. Length alone distinguishes "unset
        // placeholder" from "something key-sized but malformed" without leaking anything.
        throw new InvalidOperationException(
            $"The signing key for source connection '{connectionName}' is not a PEM-encoded private key. " +
            $"Secret '{reference.SecretName}' in '{reference.KeyVaultName}' resolved to " +
            $"{(string.IsNullOrWhiteSpace(secretValue) ? "an empty value" : $"{secretValue.Trim().Length} characters that do not begin with '-----BEGIN'")}. " +
            "Store the PKCS#8 PEM (the whole '-----BEGIN PRIVATE KEY-----' block) at that reference — or re-save " +
            "the connection using the Generate/Import signing-key source, which provisions it for you — then run again.");
    }
}
