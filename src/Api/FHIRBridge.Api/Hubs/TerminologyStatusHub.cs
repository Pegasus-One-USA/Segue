using FHIRBridge.Application.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace FHIRBridge.Api.Hubs;

/// <summary>
/// Pure server-to-client push channel for HAPI-terminology sync status (Running/Succeeded/Failed) — backs the
/// Terminology Server table on Settings → System Settings → General, replacing what used to be a 3-second
/// history poll per running system. Clients never call a method on this hub; it only ever sends
/// "TerminologyStatusChanged" (see <see cref="SignalRTerminologyStatusNotifier"/> for the event shape).
///
/// Gated <see cref="AuthorizationPolicies.SuperAdminOnly"/> rather than the plain <c>[Authorize]</c> that
/// <see cref="RunStatusHub"/> uses: the screen this backs is SuperAdmin-only (those rows carry external
/// download credentials and endpoint URLs), so the live feed about them must not be readable by anyone the
/// screen itself would refuse.
///
/// No group scoping, for the same reason as RunStatusHub: a terminology sync is an install-wide operation
/// with no tenant dimension, so every connection that is allowed in receives every system's status.
/// </summary>
[Authorize(Policy = AuthorizationPolicies.SuperAdminOnly)]
public sealed class TerminologyStatusHub : Hub
{
}
