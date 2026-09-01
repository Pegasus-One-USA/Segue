namespace FHIRBridge.Application.DTOs;

public sealed record AcceptInviteRequest(
    string Email,
    string InvitationToken,
    string Password,
    bool AcceptTerms,
    string? FirstName = null,
    string? LastName = null);
