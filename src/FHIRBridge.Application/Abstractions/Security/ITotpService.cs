namespace FHIRBridge.Application.Abstractions.Security;

/// <summary>
/// Time-based one-time password (RFC 6238) operations backing MFA. The secret is a Base32 string
/// compatible with standard authenticator apps (Microsoft/Google Authenticator, Authy, 1Password).
/// </summary>
public interface ITotpService
{
    /// <summary>Generates a new random Base32 TOTP shared secret.</summary>
    string GenerateSecret();

    /// <summary>Builds an <c>otpauth://</c> provisioning URI (rendered as a QR code by the portal).</summary>
    string BuildProvisioningUri(string secret, string accountName, string issuer);

    /// <summary>Validates a 6-digit code against the secret, tolerating ±1 time step for clock drift.</summary>
    bool ValidateCode(string secret, string code);
}
