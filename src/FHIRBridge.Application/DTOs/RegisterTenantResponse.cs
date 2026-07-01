namespace FHIRBridge.Application.DTOs;

public sealed record RegisterTenantResponse(
    Guid TenantId,
    Guid UserId,
    string AccessToken,
    string TokenType,
    DateTime ExpiresOnUtc,
    string? RefreshToken,
    DateTime? RefreshTokenExpiresOnUtc);
