using System.Security.Cryptography;
using Microsoft.Extensions.Options;

namespace FHIRBridge.LicenseServer.Security;

/// <summary>
/// Resolves the single admin account this server is gated behind. If no password is configured, generates
/// one random password ONCE per process start and logs it prominently — this keeps "just clone and run"
/// working without ever silently shipping an open door (an unset password does not mean no password).
///
/// This is intentionally simple: one shared username/password pair, no MFA, no per-user accounts. That is
/// a deliberate convenience trade-off for an internal tool — see README.md's explicit callout that stronger
/// auth (real accounts, MFA, IP allow-listing) belongs here before this handles real customer data.
/// </summary>
public sealed class AdminAccountProvider
{
    private const string DefaultUsername = "admin";

    public AdminAccountProvider(IOptions<AdminCredentialsOptions> options, ILogger<AdminAccountProvider> logger)
    {
        var config = options.Value;
        Username = string.IsNullOrWhiteSpace(config.Username) ? DefaultUsername : config.Username;

        if (!string.IsNullOrWhiteSpace(config.Password))
        {
            Password = config.Password;
            IsGeneratedPassword = false;
            return;
        }

        Password = GenerateRandomPassword();
        IsGeneratedPassword = true;

        logger.LogWarning(
            "==================================================================================\n" +
            "No AdminCredentials:Password configured. Generated a RANDOM password for THIS RUN ONLY:\n" +
            "  Username: {Username}\n" +
            "  Password: {Password}\n" +
            "This password will be DIFFERENT the next time the process restarts. Set " +
            "AdminCredentials:Password (or the AdminCredentials__Password environment variable) to pin a " +
            "real one.\n" +
            "==================================================================================",
            Username,
            Password);
    }

    public string Username { get; }

    public string Password { get; }

    /// <summary>True when <see cref="Password"/> was randomly generated for this run rather than configured
    /// — surfaced on the login page so nobody wonders why a previously-working password stopped working.</summary>
    public bool IsGeneratedPassword { get; }

    public bool Verify(string username, string password) =>
        FixedTimeEquals(username, Username) && FixedTimeEquals(password, Password);

    private static bool FixedTimeEquals(string a, string b)
    {
        var aBytes = System.Text.Encoding.UTF8.GetBytes(a);
        var bBytes = System.Text.Encoding.UTF8.GetBytes(b);

        // CryptographicOperations.FixedTimeEquals requires equal-length spans; a length mismatch alone is
        // not secret-dependent (usernames/passwords aren't compared byte-by-byte until lengths already
        // match in any real scheme), so a quick length short-circuit does not weaken this.
        if (aBytes.Length != bBytes.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(aBytes, bBytes);
    }

    private static string GenerateRandomPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
        Span<byte> randomBytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(randomBytes);

        var chars = new char[24];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[randomBytes[i] % alphabet.Length];
        }

        return new string(chars);
    }
}
