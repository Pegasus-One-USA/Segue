namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>
/// Bound from the "LicenseSigning" configuration section. Controls which ECDSA P-256 keypair this server
/// signs new licenses with — and, since this server is both the issuer and the verifier, ALSO the keypair
/// its own <c>/api/checkin</c> endpoint validates incoming tokens against (the public half is derived
/// straight from whichever private key loads here; see <see cref="LicenseSigningKeyProvider"/> for why that
/// beats keeping a second, separately-configured public-key constant in sync by hand).
/// </summary>
public sealed class LicenseSigningOptions
{
    public const string SectionName = "LicenseSigning";

    /// <summary>
    /// Path to a PFX file containing the ECDSA P-256 private key to sign with. Leave blank for local
    /// dev/testing — when blank (or the file doesn't exist), this server falls back to the throwaway dev
    /// keypair embedded in <see cref="LicenseSigningKeyProvider"/>.
    ///
    /// FOR PRODUCTION: point this at the real PegasusOne FHIRBridge Licensing PFX (10-year self-signed cert,
    /// CN=PegasusOne FHIRBridge Licensing). The user supplies that .pfx file and its password separately —
    /// this is only the config placeholder for wiring it in. Example:
    ///   "PfxPath": "C:\\secure\\pegasusone-fhirbridge-licensing.pfx"
    /// </summary>
    public string? PfxPath { get; set; }

    /// <summary>
    /// Password for <see cref="PfxPath"/>. Prefer setting this via an environment variable
    /// (<c>LicenseSigning__PfxPassword</c>) or a user-secrets/Key Vault-backed configuration provider rather
    /// than a plaintext appsettings file — see README.md's "before real customers" note.
    /// </summary>
    public string? PfxPassword { get; set; }

    /// <summary>
    /// Optional sanity check, not a source of truth: the SubjectPublicKeyInfo (base64) this server's ACTIVE
    /// signing key is expected to correspond to once the real PFX above is wired in. Purely informational —
    /// this is the exact public key value the user gave us for the real production keypair
    /// (CN=PegasusOne FHIRBridge Licensing). If set and it does NOT match the public key actually derived
    /// from <see cref="PfxPath"/> at startup, <see cref="LicenseSigningKeyProvider"/> logs a loud warning
    /// (wrong PFX file, wrong password silently falling back, etc.) — it never blocks startup.
    /// </summary>
    public string? ExpectedPublicKeyBase64 { get; set; }
}
