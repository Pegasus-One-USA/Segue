using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

/// <summary>
/// First-run initialization: after a fresh deployment (empty database) the very first SuperAdmin is created
/// interactively via the setup screen rather than seeded from config. Guarded so it can only run while no user exists.
/// </summary>
public interface ISetupService
{
    /// <summary>True when no user exists yet — the deployment still needs its first SuperAdmin.</summary>
    Task<bool> RequiresSetupAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates the first SuperAdmin (local/password) and returns a signed-in session. Throws
    /// <see cref="InvalidOperationException"/> if any user already exists (setup already completed).
    /// </summary>
    Task<LocalLoginResponse> CreateFirstSuperAdminAsync(CreateFirstSuperAdminRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Creates the first SuperAdmin from an external IdP identity (no password) and returns a signed-in
    /// session. Same one-shot guard as <see cref="CreateFirstSuperAdminAsync"/>: throws
    /// <see cref="InvalidOperationException"/> if any user already exists.
    /// </summary>
    Task<LocalLoginResponse> CreateFirstSuperAdminViaSsoAsync(CreateFirstSuperAdminSsoRequest request, CancellationToken cancellationToken);
}
