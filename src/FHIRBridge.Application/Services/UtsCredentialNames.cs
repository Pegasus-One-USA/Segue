namespace FHIRBridge.Application.Services;

/// <summary>
/// The single UTS (UMLS Terminology Services) API key RxNorm and SNOMED CT auto-sync share — one UTS
/// account/key unlocks both release families, so there is deliberately one secret rather than one per vocabulary.
/// Entering it via either the RxNorm or SNOMED settings page provisions it for both.
/// </summary>
public static class UtsCredentialNames
{
    public const string VaultName = "app";
    public const string ApiKeySecretName = "uts-api-key";
}
