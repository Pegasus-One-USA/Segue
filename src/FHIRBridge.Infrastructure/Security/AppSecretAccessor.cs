using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Domain.ValueObjects;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Singleton holder for the app-level secrets, populated once at startup by <see cref="AppSecretProvisioner"/>
/// and updatable afterward (on-demand rotation) via <see cref="Update"/>.
/// </summary>
public sealed class AppSecretAccessor : IAppSecretAccessor
{
    public string JwtSigningKey { get; private set; } = string.Empty;

    public string DownloadLinkSigningSecret { get; private set; } = string.Empty;

    public string TransformHashingKey { get; private set; } = string.Empty;

    public void Initialize(string jwtSigningKey, string downloadLinkSigningSecret, string transformHashingKey)
    {
        JwtSigningKey = jwtSigningKey;
        DownloadLinkSigningSecret = downloadLinkSigningSecret;
        TransformHashingKey = transformHashingKey;
    }

    public void Update(SecretReference secretReference, string newValue)
    {
        if (secretReference == AppSecretReferences.JwtSigningKey)
        {
            JwtSigningKey = newValue;
        }
        else if (secretReference == AppSecretReferences.DownloadLinkSigningSecret)
        {
            DownloadLinkSigningSecret = newValue;
        }
        else if (secretReference == AppSecretReferences.TransformHashingKey)
        {
            TransformHashingKey = newValue;
        }
        else
        {
            throw new ArgumentException($"Unknown app secret reference '{secretReference.SecretName}'.", nameof(secretReference));
        }
    }
}
