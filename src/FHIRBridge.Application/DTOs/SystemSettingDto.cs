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

/// <summary>One key/value pair within a BatchSetSystemSettingsRequest — same fields as
/// SetSystemSettingRequest plus the key, since a batch covers multiple keys at once.</summary>
public sealed record BatchSystemSettingItem(string Key, string Value, string? Description);

/// <summary>Saves every setting in an arbitrary group (e.g. all "AlertEvaluation:*" keys) in one call,
/// so a group-edit dialog gets one Update action instead of one PUT per field. Unlike the 13 Terminology
/// systems, General Settings groups aren't a fixed, known set — this stays generic rather than typed.</summary>
public sealed record BatchSetSystemSettingsRequest(IReadOnlyList<BatchSystemSettingItem> Items);
