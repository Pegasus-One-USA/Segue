namespace FHIRBridge.Application.DTOs;

/// <summary>First-run request to create the sole SuperAdmin (local/password path). Only honored when no user exists.</summary>
public sealed record CreateFirstSuperAdminRequest(
    string Email,
    string? DisplayName,
    string Password);

/// <summary>Reports whether the deployment still needs its first SuperAdmin created.</summary>
public sealed record SetupStatusDto(bool RequiresSetup);
