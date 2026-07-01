namespace FHIRBridge.Application.DTOs;

public sealed record LocalLoginRequest(
    string Email,
    string Password);
