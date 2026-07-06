using System.Security.Cryptography;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Security;

/// <summary>
/// Generation and one-time verification of MFA backup codes. Codes are shown to the user once, in
/// plaintext, at enrollment; only their hashes are persisted (via <see cref="IPasswordHasher"/>).
/// </summary>
public static class MfaBackupCodes
{
    // Unambiguous alphabet (no 0/O/1/I) for codes users may transcribe by hand.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 10;

    public static IReadOnlyList<string> Generate(int count)
    {
        var codes = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var chars = new char[CodeLength];
            for (var c = 0; c < CodeLength; c++)
            {
                chars[c] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }

            // Group as XXXXX-XXXXX for readability.
            codes.Add($"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}");
        }

        return codes;
    }

    /// <summary>
    /// Verifies a supplied backup code against the user's stored hashes and, on a match, consumes it
    /// so it can never be reused. Returns true when a code was matched and consumed.
    /// </summary>
    public static bool TryConsume(User user, string code, IPasswordHasher passwordHasher)
    {
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrEmpty(user.MfaBackupCodeHashes))
        {
            return false;
        }

        var normalized = code.Trim().ToUpperInvariant();
        var hashes = user.MfaBackupCodeHashes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var hash in hashes)
        {
            if (passwordHasher.Verify(normalized, hash))
            {
                user.TryConsumeBackupCode(hash);
                return true;
            }
        }

        return false;
    }
}
