namespace FHIRBridge.Application.DTOs;

/// <summary>What an EHR vendor accepts writes for, as the portal's pickers need it.</summary>
/// <param name="Vendor">The vendor name as given, or null when none was.</param>
/// <param name="Capabilities">Empty when the vendor accepts no writes.</param>
/// <param name="CloneModeEnabled">The QA-only clone-mode system setting, exposed here because non-admin users cannot
/// read system settings and the destination form must know whether to offer clone mode.</param>
public sealed record EhrWriteCapabilitiesDto(
    string? Vendor,
    bool SupportsPatientMatch,
    bool CloneModeEnabled,
    IReadOnlyList<EhrWriteCapabilityDto> Capabilities);

public sealed record EhrWriteCapabilityDto(
    string ResourceType,
    IReadOnlyList<string> Operations,
    string VendorApiId,
    string? Variant,
    bool RequiresEncounter,
    bool OptInOnly,
    IReadOnlyList<string> AllowedApplicationTypes);
