namespace FHIRBridge.SharedKernel.Exceptions;

/// <summary>
/// A referenced secret (connection string, API key, ...) has no value configured for its vault/name. Deliberately
/// NOT an <see cref="FHIRBridgeException"/> subtype: those are always mapped to a client (4xx) response and skip
/// <c>ErrorLogs</c> as routine/expected outcomes. A missing secret is a genuine deployment/configuration fault, not
/// something the caller did wrong, so the API's global exception handler maps this to 500 and routes it through the
/// Global Exception Manager like any other unexpected failure.
/// </summary>
public sealed class SecretNotConfiguredException : Exception
{
    public SecretNotConfiguredException(string secretName, string keyVaultName, string? message = null)
        : base(message ?? $"Secret '{secretName}' was not found for vault '{keyVaultName}'.")
    {
        SecretName = secretName;
        KeyVaultName = keyVaultName;
    }

    public string SecretName { get; }
    public string KeyVaultName { get; }
}
