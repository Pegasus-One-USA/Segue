namespace FHIRBridge.Application.DTOs;

public sealed record AcceptInviteRequest(
    string Email,
    string InvitationToken,
    string Password,
    string? FirstName,
    string? LastName);
