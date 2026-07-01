using System.Security.Cryptography;
using System.Text;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

/// <summary>
/// Proof Key for Code Exchange (RFC 7636) helpers for the S256 method used by public OAuth clients such as Healow.
/// The verifier is a high-entropy random string; the challenge is its base64url-encoded SHA-256 digest.
/// </summary>
public static class Pkce
{
    /// <summary>Generates a 43-character (32-byte) base64url code verifier.</summary>
    public static string CreateCodeVerifier()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(bytes);
    }

    /// <summary>Computes the S256 code challenge for a verifier.</summary>
    public static string CreateS256Challenge(string codeVerifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier));
        return Base64UrlEncode(hash);
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}
