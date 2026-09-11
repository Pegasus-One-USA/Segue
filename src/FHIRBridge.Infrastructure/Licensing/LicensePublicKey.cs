namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// The ECDSA P-256 (ES256) public key <see cref="SignedLicenseValidator"/> uses to verify every signed
/// license token, as an SubjectPublicKeyInfo blob base64-encoded (i.e. exactly what
/// <c>ECDsa.ExportSubjectPublicKeyInfo()</c> produces).
///
/// DEV-ONLY KEYPAIR — checked in only so `dotnet test`, local dev, and
/// <c>tools/FHIRBridge.LicenseMinter</c> (via its <c>--dev-key</c> flag) work out of the box with no
/// external setup. The matching private key lives only in the minting tool's dev-fallback config,
/// clearly commented there as throwaway/never-for-real-customers. It was generated once, ad hoc, via:
/// <code>
/// using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
/// var publicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
/// var privateKeyBase64 = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
/// </code>
///
/// Before shipping to the first real customer, PegasusOne MUST generate a production keypair, store the
/// private key ONLY in Key Vault (never in source control, never in this tool's config), and replace
/// <see cref="PublicKeyBase64"/> below with the new public key. Every license token signed with the dev
/// private key above will stop validating the moment that swap happens — which is the point.
/// </summary>
public static class LicensePublicKey
{
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJzDmVl5Hl9OkqGclWNjRQkIwDw/XVwpsqGQoXN5+ffgh28DCw4PJc3Lof+tDWzkM3xywf3xsblKUzPp+ZdrCaw==";
}
