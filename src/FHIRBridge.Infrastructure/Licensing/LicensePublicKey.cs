namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// The ECDSA P-256 (ES256) public key <see cref="SignedLicenseValidator"/> uses to verify every signed
/// license token, as an SubjectPublicKeyInfo blob base64-encoded (i.e. exactly what
/// <c>ECDsa.ExportSubjectPublicKeyInfo()</c> produces).
///
/// PRODUCTION KEY — matches the private key in the real PegasusOne FHIRBridge Licensing PFX (10-year
/// self-signed cert, CN=PegasusOne FHIRBridge Licensing, generated 2026-09-21). The private key is held
/// ONLY in that PFX, configured as FHIRBridge-LicenseServer's <c>LicenseSigning:PfxPath</c>/
/// <c>PfxPassword</c> — never in source control, never in this repo. Deliberately NOT read from any
/// config file here either (see this class's own history/PR discussion): the trusted public key must be
/// compiled into the shipped binary, not attacker-editable via a config file an install's own
/// filesystem access could reach — that would let anyone who can edit config mint themselves a
/// "valid" license against their own keypair. <c>tools/FHIRBridge.LicenseMinter</c>'s <c>--dev-key</c>
/// flag still uses its own separate, clearly-marked throwaway dev keypair for local testing, and
/// <c>tests/FHIRBridge.UnitTests/Licensing/SignedLicenseValidatorTests</c> validates against its own
/// throwaway keypair via <c>SignedLicenseValidator.ValidateWithPublicKey</c> — neither has ever matched
/// this constant, and neither needs to.
///
/// Rotating this requires re-issuing every license already minted against the old key: every deployed
/// FHIRBridge install stops trusting that old key the instant it upgrades to a build with this constant
/// changed.
/// </summary>
public static class LicensePublicKey
{
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5hSsY3VXxFDV03RxN4P4aFkCrgTeMB5op1N+MaUrd0KtUww8vcEFUqezfhJ5DFh9wmACnTYoCqiQDbdIDRHIAg==";
}
