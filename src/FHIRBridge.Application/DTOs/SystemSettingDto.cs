namespace FHIRBridge.Application.DTOs;

public sealed record SystemSettingDto(
    Guid Id,
    string Key,
    string Value,
    string? Description,
    DateTime CreatedOnUtc,
    DateTime? ModifiedOnUtc);

public sealed record SetSystemSettingRequest(string Value, string? Description);
