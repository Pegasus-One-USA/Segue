namespace FHIRBridge.Application.DTOs;

public sealed record AccessTokenDto(
    string AccessToken,
    string TokenType,
    DateTime ExpiresOnUtc);
