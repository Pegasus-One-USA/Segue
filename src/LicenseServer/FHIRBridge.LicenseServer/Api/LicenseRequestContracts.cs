namespace FHIRBridge.LicenseServer.Api;

/// <summary>Request body for <c>POST /api/license-requests</c> — mirrors the main repo's own
/// <c>LicenseRequestIntakeBody</c> (src/FHIRBridge.Infrastructure/Licensing/LicenseRequestService.cs)
/// field for field, since that's exactly what posts here.</summary>
public sealed record LicenseRequestIntakeBody(
    string ClientName, string Email, string? CompanyName, string? Address, string PhoneNumber, string UniqueKey,
    string? RequestHost = null);

/// <summary>Error body returned on a rejected (400) intake request.</summary>
public sealed record LicenseRequestIntakeError(string Error);
