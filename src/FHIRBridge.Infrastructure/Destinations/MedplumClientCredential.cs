namespace FHIRBridge.Infrastructure.Destinations;

/// <summary>
/// How a Medplum <c>ClientApplication</c> authenticates at the token endpoint. Both are OAuth2
/// <c>grant_type=client_credentials</c> — they differ only in how the client proves itself.
/// </summary>
public abstract record MedplumClientCredential(string ClientId);

/// <summary>Symmetric secret (<c>client_secret</c>) — the simplest machine-to-machine option.</summary>
public sealed record MedplumClientSecretCredential(string ClientId, string ClientSecret)
    : MedplumClientCredential(ClientId);

/// <summary>
/// SMART Backend Services asymmetric auth: a short-lived RS384 JWT client assertion signed with the client's private
/// key, whose public key Medplum fetches from the <c>ClientApplication.jwksUri</c>. This is the same flow FHIRBridge's
/// Epic connector uses; see docs/backend/15-medplum-integration-plan.md §3.
/// </summary>
public sealed record MedplumPrivateKeyJwtCredential(string ClientId, string PrivateKeyPem, string? KeyId)
    : MedplumClientCredential(ClientId);
