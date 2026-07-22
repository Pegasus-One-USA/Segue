using FHIRBridge.Application.Abstractions.Security;

namespace FHIRBridge.Infrastructure.Security;

/// <summary>
/// Fallback <see cref="ICurrentUserService"/> for hosts without an HTTP request context (e.g. the Worker,
/// background services, migrations). Reports a non-interactive actor so audit stamping still works — labeled
/// by whatever automation is currently running (<see cref="IAmbientActorContext"/>, e.g. "Scheduler
/// (Automated Pipeline Run)" or "Webhook Ingestion (Automated)"), falling back to the generic "system" when
/// no automation has set a scope (e.g. outside any request, such as at host startup).
/// Registered via TryAdd so an HTTP-aware implementation (in the API host) takes precedence when present.
/// </summary>
public sealed class SystemCurrentUserService : ICurrentUserService
{
    private readonly IAmbientActorContext _ambientActorContext;

    public SystemCurrentUserService(IAmbientActorContext ambientActorContext)
    {
        _ambientActorContext = ambientActorContext;
    }

    public CurrentUserInfo CurrentUser
    {
        get
        {
            var actor = _ambientActorContext.Current;
            return new CurrentUserInfo(
                ExternalUserId: actor ?? "system",
                Email: null,
                DisplayName: actor ?? "System",
                Roles: [],
                IsAuthenticated: false);
        }
    }
}
