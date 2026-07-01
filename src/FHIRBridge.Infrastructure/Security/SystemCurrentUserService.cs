using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Fallback <see cref="ICurrentUserService"/> for hosts without an HTTP request context (e.g. the Worker,
/// background services, migrations). Reports a non-interactive "system" actor so audit stamping still works.
/// Registered via TryAdd so an HTTP-aware implementation (in the API host) takes precedence when present.
/// </summary>
public sealed class SystemCurrentUserService : ICurrentUserService
{
    private static readonly CurrentUserInfo System = new(
        ExternalUserId: "system",
        Email: null,
        DisplayName: "System",
        Roles: [],
        IsAuthenticated: false);

    public CurrentUserInfo CurrentUser => System;
}
