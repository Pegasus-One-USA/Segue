namespace FHIRBridge.Runtime.Application.DTOs;

/// <summary>The redirect URL the user is sent to, plus the PKCE verifier and state that must be retained to
/// complete the interactive sign-in from the OAuth callback.</summary>
public sealed record SmartAuthorizationRequest(string AuthorizationUrl, string CodeVerifier, string State);
