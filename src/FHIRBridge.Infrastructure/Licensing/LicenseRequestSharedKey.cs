namespace FHIRBridge.Infrastructure.Licensing;

/// <summary>
/// AES-256 key shared between every FHIRBridge install and the licensor's intake tooling
/// (FHIRBridge-LicenseServer, a separate repository — see that repo's own copy of this same constant),
/// used only to encode <see cref="LicenseRequestPayloadEncoder"/>'s manual-fallback blob when the direct
/// <c>POST /api/v1/license-request</c> outbound call can't reach the licensor.
///
/// This is NOT a defense against a sophisticated attacker — it's baked into every shipped binary, so
/// anyone who reverse-engineers the product can recover it, same as any embedded symmetric key. The real
/// integrity property this feature relies on is <c>LicenseRequest.UniqueKey</c> being checked against the
/// license's own <c>requestKey</c> claim at apply time (see <c>LicenseService.ApplyAsync</c>), which this
/// key plays no part in. Its only job is keeping the copy-pasted fallback blob opaque and tamper-evident
/// for support purposes (an admin can't casually edit their own contact details in transit), not
/// cryptographically unforgeable.
///
/// PLACEHOLDER — generated once, ad hoc, for initial development. Rotating it requires updating BOTH
/// repositories in lockstep (an old blob encoded with a since-rotated key will fail to decode, which is
/// safe — it just fails the manual-fallback path, never the direct API call or license activation).
/// </summary>
public static class LicenseRequestSharedKey
{
    public const string KeyBase64 = "N4vRYHvMVtduSsBjRly3kJKsXJyAxTjXrC0tL8S53hA=";
}
