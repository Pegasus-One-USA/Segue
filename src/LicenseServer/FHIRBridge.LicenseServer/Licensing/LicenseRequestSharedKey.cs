namespace FHIRBridge.LicenseServer.Licensing;

/// <summary>
/// AES-256 key shared with every FHIRBridge install (see that repo's own copy of this exact class:
/// src/FHIRBridge.Infrastructure/Licensing/LicenseRequestSharedKey.cs) — used only to decode the
/// manual-fallback blob an operator pastes on <c>Pages/LicenseRequests/Index</c> when a customer's direct
/// <c>POST /api/license-requests</c> call couldn't reach this server.
///
/// <see cref="KeyBase64"/> MUST resolve to the exact same value as the main repo's copy, byte for byte —
/// see that file's own remarks for what security property this key does (and does not) provide, and for
/// what rotating it requires. Resolves from <see cref="EnvVar"/> when set (same env var name as the main
/// repo), falling back to the same committed, publicly-known dev placeholder otherwise — see
/// <see cref="IsUsingDevPlaceholder"/>.
/// </summary>
public static class LicenseRequestSharedKey
{
    /// <summary>Base64-encoded 32-byte AES-256 key — MUST be set to the exact same value as the matching
    /// customer install's own FHIRBRIDGE_LICENSE_REQUEST_SHARED_KEY for anything beyond local development.</summary>
    private const string EnvVar = "FHIRBRIDGE_LICENSE_REQUEST_SHARED_KEY";

    /// <summary>DEV-ONLY — see this class's remarks. Never treat this as a real secret.</summary>
    private const string DevPlaceholderKeyBase64 = "N4vRYHvMVtduSsBjRly3kJKsXJyAxTjXrC0tL8S53hA=";

    private static readonly string? ConfiguredKeyBase64 = Environment.GetEnvironmentVariable(EnvVar);

    /// <summary>True when no <see cref="EnvVar"/> value was found and the code fell back to the
    /// committed, publicly-known dev placeholder.</summary>
    public static bool IsUsingDevPlaceholder => string.IsNullOrWhiteSpace(ConfiguredKeyBase64);

    public static string KeyBase64 => IsUsingDevPlaceholder ? DevPlaceholderKeyBase64 : ConfiguredKeyBase64!;
}
