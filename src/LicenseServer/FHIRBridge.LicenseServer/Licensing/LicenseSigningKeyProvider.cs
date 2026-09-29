using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>
/// Singleton holder of the ONE ECDSA P-256 keypair this server signs and verifies FHIRBridge license
/// tokens with. Loaded once at startup and kept alive for the process lifetime (never disposed) — the same
/// pattern the main FHIRBridge repo's SignedLicenseValidator documents: Microsoft.IdentityModel.Tokens'
/// process-wide CryptoProviderFactory caches SignatureProviders keyed by key material, so importing-then-
/// disposing an ECDsa per call can hand a later call back a provider wrapping an already-disposed key.
///
/// Deliberate design choice vs. the main repo's split of "LicensePublicKey.cs" (a hardcoded public-key
/// constant used only to verify) and a separately-configured private key used only to sign: THIS project
/// mints AND verifies in the same process, so instead of maintaining two constants that must always agree,
/// the public key used for verification is always derived directly from whichever private key is active
/// (<see cref="ECDsa.ExportSubjectPublicKeyInfo"/> exports only the public component, even when the ECDsa
/// instance holds a private key too). That makes a dev/prod key mismatch structurally impossible instead of
/// just documented — see <see cref="LicenseSigningOptions.ExpectedPublicKeyBase64"/> for an optional extra
/// sanity check on top.
///
/// DEV-ONLY KEYPAIR embedded below — deliberately the SAME keypair as the main FHIRBridge repo's own dev
/// key (<c>LicensePublicKey.cs</c> / <c>tools/FHIRBridge.LicenseMinter</c>'s <c>--dev-key</c>), not a
/// separately-generated one. Two independently-generated dev keypairs (one per project) would mean a token
/// minted here with no PfxPath configured could never validate against the main repo's default dev public
/// key — exactly the mismatch that motivated sharing one. This keypair must NEVER be used to sign a license
/// for a real customer. Every license token signed with it stops validating the instant a real PFX is
/// configured via <see cref="LicenseSigningOptions.PfxPath"/>, which is exactly the point.
/// </summary>
public sealed class LicenseSigningKeyProvider
{
    /// <summary>DEV-ONLY private key (PKCS8, base64). Never use for a real customer license.
    /// Deliberately the SAME keypair as the main FHIRBridge repo's own dev key
    /// (src/FHIRBridge.Infrastructure/Licensing/LicensePublicKey.cs / tools/FHIRBridge.LicenseMinter) —
    /// this project originally generated its own separate throwaway dev key, which meant a token minted
    /// here with no PfxPath configured could never validate against the main repo's default embedded dev
    /// public key. Sharing one dev keypair across both projects makes local end-to-end testing (mint here,
    /// activate there) work out of the box with zero configuration.</summary>
    private const string DevPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgzNfRUjAwdWWhtF6HDTFMjzkpc6ix4tAt0KpVuBS1H+mhRANCAAQnMOZWXkeX06SoZyVY2NFCQjAPD9dXCmyoZChc3n59+CHbwMLDg8lzcuh/60NbOQzfHLB/fGxuUpTM+n5l2sJr";

    /// <summary>Matching DEV-ONLY public key (SubjectPublicKeyInfo, base64) — informational only; the
    /// active <see cref="ECDsa"/> below always re-derives this itself rather than trusting this constant.</summary>
    public const string DevPublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJzDmVl5Hl9OkqGclWNjRQkIwDw/XVwpsqGQoXN5+ffgh28DCw4PJc3Lof+tDWzkM3xywf3xsblKUzPp+ZdrCaw==";

    public LicenseSigningKeyProvider(IOptions<LicenseSigningOptions> options, ILogger<LicenseSigningKeyProvider> logger)
    {
        var config = options.Value;
        var (ecdsa, usingDevKey) = LoadKey(config, logger);

        Key = ecdsa;
        IsUsingDevKey = usingDevKey;
        PublicKeyBase64 = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        if (usingDevKey)
        {
            logger.LogWarning(
                "License signing key: using the EMBEDDED DEV-ONLY keypair (no PfxPath configured, or the " +
                "file/password did not load). Tokens minted right now will NOT match production licenses. " +
                "Configure LicenseSigning:PfxPath / LicenseSigning:PfxPassword to use the real signing key.");
        }
        else
        {
            logger.LogInformation(
                "License signing key: loaded from {PfxPath}. Active public key (SubjectPublicKeyInfo, base64): {PublicKey}",
                config.PfxPath,
                PublicKeyBase64);

            if (!string.IsNullOrWhiteSpace(config.ExpectedPublicKeyBase64) &&
                !string.Equals(config.ExpectedPublicKeyBase64.Trim(), PublicKeyBase64, StringComparison.Ordinal))
            {
                logger.LogWarning(
                    "License signing key: the key loaded from {PfxPath} does NOT match " +
                    "LicenseSigning:ExpectedPublicKeyBase64. Double-check this is the right PFX file — " +
                    "tokens signed with it will not verify against the public key everyone expects.",
                    config.PfxPath);
            }
        }
    }

    /// <summary>The active ECDSA keypair (private key present) — used both to sign new tokens and, via its
    /// public component, to verify incoming ones. Never disposed for the process lifetime; see class
    /// remarks.</summary>
    public ECDsa Key { get; }

    /// <summary>True when no production PFX was configured/loadable and this process is signing with the
    /// embedded throwaway dev keypair. Surfaced in the admin UI so nobody mistakes a dev-signed license for
    /// a real one.</summary>
    public bool IsUsingDevKey { get; }

    /// <summary>SubjectPublicKeyInfo of the currently active key, base64-encoded — always derived from
    /// <see cref="Key"/>, never a hand-maintained constant.</summary>
    public string PublicKeyBase64 { get; }

    private static (ECDsa Ecdsa, bool UsingDevKey) LoadKey(LicenseSigningOptions config, ILogger logger)
    {
        if (!string.IsNullOrWhiteSpace(config.PfxPath) && File.Exists(config.PfxPath))
        {
            try
            {
                using var cert = X509CertificateLoader.LoadPkcs12FromFile(
                    config.PfxPath,
                    config.PfxPassword,
                    X509KeyStorageFlags.Exportable);
                var privateKey = cert.GetECDsaPrivateKey();
                if (privateKey is null)
                {
                    logger.LogWarning(
                        "License signing key: {PfxPath} loaded but contains no ECDSA private key. Falling back to the dev key.",
                        config.PfxPath);
                }
                else
                {
                    // Copy the key out of the certificate's handle so it outlives the `using var cert` disposal.
                    var exported = privateKey.ExportPkcs8PrivateKey();
                    var standalone = ECDsa.Create();
                    standalone.ImportPkcs8PrivateKey(exported, out _);
                    return (standalone, false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "License signing key: failed to load {PfxPath}. Falling back to the dev key.", config.PfxPath);
            }
        }

        var dev = ECDsa.Create();
        dev.ImportPkcs8PrivateKey(Convert.FromBase64String(DevPrivateKeyPkcs8Base64), out _);
        return (dev, true);
    }
}
