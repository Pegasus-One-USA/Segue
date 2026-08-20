namespace FHIRBridge.Application.DTOs;

/// <summary>Requests a passwordless "sign-in link" email for the given address.</summary>
public sealed record MagicLinkRequest(string Email);

/// <summary>
/// Always reports success regardless of whether an account exists for the address, matching
/// <see cref="ForgotPasswordResponse"/>'s no-user-enumeration shape.
/// </summary>
public sealed record MagicLinkResponse(bool Accepted);

/// <summary>Redeems a magic-link token to complete sign-in.</summary>
public sealed record MagicLinkRedeemRequest(string Email, string Token);
