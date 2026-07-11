using FHIRBridge.Domain.Enums;

namespace FHIRBridge.Application.DTOs;

/// <summary>
/// Interactive (authorization-code) settings for a source connection: the registered redirect URI(s), the
/// standalone launch URL, the patient-selection method, and — for EHR launch — the trusted-issuer allow-list. Null
/// on Backend sources.
/// </summary>
public sealed record SourceInteractiveConfigurationDto(
    string[] RedirectUris,
    string? LaunchUrl,
    string[] TrustedIssuers,
    PatientSelectionMethod? PatientSelectionMethod = null,
    string? PostLaunchRedirectUri = null);
