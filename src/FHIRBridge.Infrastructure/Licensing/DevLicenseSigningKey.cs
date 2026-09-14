namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// ⚠️⚠️⚠️ TEMPORARY / DEV-ONLY — DO NOT SHIP, DO NOT USE FOR ANY REAL CUSTOMER ⚠️⚠️⚠️
///
/// Holds the exact same throwaway ECDSA P-256 private key as <c>tools/FHIRBridge.LicenseMinter</c>'s
/// <c>--dev-key</c> fallback (see that file's header comment for full provenance/generation history, and
/// <see cref="LicensePublicKey"/> for the matching public key this must verify against). Duplicated here
/// ONLY so the portal's temporary "Dev: Mint a test license" page
/// (<c>portal/src/app/settings/pages/license-dev-mint</c>) can sign a license token in-process, via
/// <see cref="DevLicenseMintingService"/> / <see cref="IDevLicenseMintingService"/> and
/// <c>FHIRBridge.Api.Controllers.V1.DevLicenseMintingController</c>, without shelling out to the standalone
/// CLI tool.
///
/// This is the SAME key as the tool's — not regenerated — so a token minted through this temporary page
/// verifies successfully against the real, unmodified <c>SignedLicenseValidator</c> /
/// <see cref="LicensePublicKey"/> used everywhere else in the product.
///
/// This whole capability (sign a license) is one the product's real API/Infrastructure code deliberately
/// NEVER has outside this throwaway page — see <see cref="LicensePublicKey"/>'s remarks: the shipped
/// product can only VERIFY licenses. The only thing standing between this private key and the public
/// internet is <c>DevLicenseMintingController</c>'s <c>IHostEnvironment.IsDevelopment()</c> gate, which
/// 404s the endpoint on every non-Development host. Never remove that gate, and never let this class (or
/// anything that reads it) be reachable from a non-Development-hosted process.
///
/// DELETE THIS FILE — along with <see cref="IDevLicenseMintingService"/> /
/// <see cref="DevLicenseMintingService"/> and <c>DevLicenseMintingController</c> — once license minting
/// moves to its own separate internal tool/portal, which was always the plan for this temporary stand-in.
/// </summary>
public static class DevLicenseSigningKey
{
    /// <summary>Same value as <c>tools/FHIRBridge.LicenseMinter</c>'s <c>DevPrivateKeyPkcs8Base64</c> —
    /// copied verbatim, never regenerated, so tokens minted here verify against the existing
    /// <see cref="LicensePublicKey.PublicKeyBase64"/>.</summary>
    public const string DevPrivateKeyPkcs8Base64 =
        "MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQgzNfRUjAwdWWhtF6HDTFMjzkpc6ix4tAt0KpVuBS1H+mhRANCAAQnMOZWXkeX06SoZyVY2NFCQjAPD9dXCmyoZChc3n59+CHbwMLDg8lzcuh/60NbOQzfHLB/fGxuUpTM+n5l2sJr";
}
