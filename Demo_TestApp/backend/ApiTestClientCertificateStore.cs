using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace HealthAppBackend;

/// <summary>
/// Mints a single self-signed RSA client certificate on startup and holds both halves of it: the base64 PFX (for
/// pasting into a FHIRBridge ApiEndpoint destination's secret field) and the thumbprint the receiver checks the
/// TLS-presented client certificate against. Same "this app plays both sides" reasoning as
/// <see cref="ApiTestOAuth2TokenStore"/>/<see cref="DataLakeTokenStore"/> — no real PKI is worth standing up for a
/// demo receiver, and pinning the thumbprint (rather than validating a chain) is enough to prove the destination
/// actually presented the certificate it was configured with.
///
/// Regenerated every process start — nothing persists it — so the thumbprint published by
/// <c>GET /api/apitest/auth/client-certificate/credential</c> is always the one this running instance will accept;
/// a destination configured against a previous run's PFX will need the fresh one after a backend restart.
/// </summary>
public sealed class ApiTestClientCertificateStore
{
    public string PfxBase64 { get; }
    public string Thumbprint { get; }

    /// <summary>Ready-to-paste secret value in the exact "&lt;pfxBase64&gt;|&lt;password&gt;" shape
    /// ApiEndpointSender's ClientCertificate mode expects.</summary>
    public string FormattedSecret => $"{PfxBase64}|{ApiTestAuthCredentials.ClientCertificatePfxPassword}";

    public ApiTestClientCertificateStore()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=FHIRBridge ApiEndpoint Test Client",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                [new Oid("1.3.6.1.5.5.7.3.2")], // Client Authentication
                critical: false));

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));

        var pfxBytes = certificate.Export(X509ContentType.Pfx, ApiTestAuthCredentials.ClientCertificatePfxPassword);
        PfxBase64 = Convert.ToBase64String(pfxBytes);
        Thumbprint = certificate.Thumbprint;
    }
}
