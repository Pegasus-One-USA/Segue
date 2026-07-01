using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// PBKDF2-SHA256 password hasher. Produces a self-contained string that embeds the salt and
/// iteration count so the format is upgradeable without breaking existing hashes.
/// Format: "v1:{iterations}:{base64-salt}:{base64-hash}"
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const int Iterations = 350_000;
    private const int SaltBytes = 32;
    private const int HashBytes = 32;
    private const string Prefix = "v1:";

    public string Hash(string value)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Pbkdf2(value, salt, Iterations);

        return $"{Prefix}{Iterations}:{Convert.ToBase64String(salt)}:{Convert.ToBase64String(hash)}";
    }

    public bool Verify(string value, string storedHash)
    {
        if (string.IsNullOrWhiteSpace(storedHash))
        {
            return false;
        }

        if (!storedHash.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var parts = storedHash[Prefix.Length..].Split(':');
        if (parts.Length != 3 ||
            !int.TryParse(parts[0], out var iterations) ||
            iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[1]);
            var expectedHash = Convert.FromBase64String(parts[2]);
            var actualHash = Pbkdf2(value, salt, iterations);

            return CryptographicOperations.FixedTimeEquals(actualHash, expectedHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
    }
}
