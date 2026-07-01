namespace FHIRBridge.Application.DTOs;

public sealed record ChangePasswordRequest(
    string CurrentPassword,
    string NewPassword);
