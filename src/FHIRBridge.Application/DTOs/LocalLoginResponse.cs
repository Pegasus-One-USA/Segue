namespace FHIRBridge.Application.DTOs;

public sealed record LocalLoginResponse(
    string AccessToken,
    string TokenType,
    DateTime ExpiresOnUtc,
    bool RequiresPasswordChange,
    UserProfileDto Profile);
