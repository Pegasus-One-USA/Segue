namespace FHIRBridge.Application.DTOs;

public sealed record ForgotPasswordResponse(
    bool Accepted,
    string? ResetToken,
    DateTime? ExpiresOnUtc);
