namespace FHIRBridge.Application.DTOs;

public sealed record ResetPasswordRequest(
    string Email,
    string ResetToken,
    string NewPassword);
