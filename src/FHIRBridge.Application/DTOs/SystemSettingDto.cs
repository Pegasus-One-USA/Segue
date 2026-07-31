namespace FHIRBridge.Application.DTOs;

public sealed record SystemSettingDto(
    Guid Id,
    string Key,
    string Value,
    string? Description,
    DateTime CreatedOnUtc,
    DateTime? ModifiedOnUtc,
    string? CreatedBy = null,
    string? ModifiedBy = null);

public sealed record SetSystemSettingRequest(string Value, string? Description);
