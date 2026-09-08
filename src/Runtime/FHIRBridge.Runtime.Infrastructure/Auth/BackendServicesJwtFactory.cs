using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FHIRBridge.Runtime.Application.Abstractions.Auth;

namespace FHIRBridge.Runtime.Infrastructure.Auth;

public sealed class BackendServicesJwtFactory : IBackendServicesJwtFactory
{
    public string CreateClientAssertion(BackendServicesJwtRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.PrivateKeyPem))
        {
            throw new InvalidOperationException("SMART Backend Services private key PEM is missing.");
        }

        using var rsa = RSA.Create();
        try
        {
            rsa.ImportFromPem(request.PrivateKeyPem);
        }
        catch (ArgumentException exception)
        {
            // RSA.ImportFromPem's own message ("...not the path to such a file") blames a file path, which is
            // almost never the real cause — a secret store returning an unset placeholder is. Callers that resolve
            // the key from a secret reference diagnose this earlier and can name the exact (vault, secret): see
            // SigningKeySecretGuard.EnsurePemShaped. This is the backstop for every other caller, and
            // deliberately says nothing about the value itself, which is key material when it is valid.
            throw new InvalidOperationException(
                "The configured SMART Backend Services signing key is not a PEM-encoded private key. Store the " +
                "PKCS#8 PEM (the whole '-----BEGIN PRIVATE KEY-----' block) at the connection's configured " +
                "signing-key secret reference, or re-provision it via the Generate/Import signing-key source.",
                exception);
        }

        var now = DateTimeOffset.UtcNow;
        var expires = now.Add(request.Lifetime);
        var jwtId = Guid.NewGuid().ToString("N");

        var header = new Dictionary<string, object?>
        {
            ["alg"] = "RS384",
            ["typ"] = "JWT"
        };

        if (!string.IsNullOrWhiteSpace(request.KeyId))
        {
            header["kid"] = request.KeyId;
        }

        var payload = new Dictionary<string, object>
        {
            ["iss"] = request.ClientId,
            ["sub"] = request.ClientId,
            ["aud"] = request.TokenEndpoint,
            ["jti"] = jwtId,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-30).ToUnixTimeSeconds(),
            ["exp"] = expires.ToUnixTimeSeconds()
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var signingInput = $"{encodedHeader}.{encodedPayload}";
        var signature = rsa.SignData(
            Encoding.UTF8.GetBytes(signingInput),
            HashAlgorithmName.SHA384,
            RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64UrlEncode(signature)}";
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
