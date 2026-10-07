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

/// <param name="LiveWriteSupported">The product can send this type live (it is not dry-run-only in code): a destination
/// that selects it and is not a dry run sends it, once <paramref name="RequiresVendorActivation"/> is satisfied.</param>
/// <param name="RequiresVendorActivation">Sent live only over a connection whose vendor write APIs are activated
/// (eClinicalWorks contracted APIs, athenaOne).</param>
/// <param name="CreatesHolderEncounter">Filed on an encounter the bridge creates, only when the destination opts in
/// (eClinicalWorks medical and surgical history).</param>
public sealed record EhrWriteCapabilityDto(
    string ResourceType,
    IReadOnlyList<string> Operations,
    string VendorApiId,
    string? Variant,
    bool RequiresEncounter,
    bool OptInOnly,
    IReadOnlyList<string> AllowedApplicationTypes,
    bool LiveWriteSupported,
    bool RequiresVendorActivation = false,
    bool CreatesHolderEncounter = false);
