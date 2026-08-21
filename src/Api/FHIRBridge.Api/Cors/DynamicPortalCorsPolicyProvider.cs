using FHIRBridge.Application.Abstractions.Caching;
using Microsoft.AspNetCore.Cors.Infrastructure;

namespace FHIRBridge.Api.Cors;

/// <summary>
/// Builds the "Portal" CORS policy from <see cref="IAllowedCorsOriginsCache"/> on every request, instead
/// of the fixed <c>WithOrigins(...)</c> list AddCors would otherwise bake in at startup — so an origin
/// added/removed through the admin screen takes effect on the very next request, no restart.
/// </summary>
public sealed class DynamicPortalCorsPolicyProvider : ICorsPolicyProvider
{
    public const string PortalPolicyName = "Portal";

    private readonly IAllowedCorsOriginsCache _cache;

    public DynamicPortalCorsPolicyProvider(IAllowedCorsOriginsCache cache)
    {
        _cache = cache;
    }

    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (!string.Equals(policyName, PortalPolicyName, StringComparison.Ordinal))
        {
            return null;
        }

        var allowedOrigins = await _cache.GetOriginsAsync(context.RequestAborted);

        // Narrowed from AllowAnyHeader/AllowAnyMethod (HIPAA/SOC2 CC6.1): a credentialed CORS policy
        // should expose only the verbs and headers the portal actually uses.
        return new CorsPolicyBuilder()
            .SetIsOriginAllowed(origin => allowedOrigins.Contains(origin))
            .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS")
            // X-Requested-With and X-SignalR-User-Agent are both sent unconditionally by @microsoft/signalr on
            // every negotiate call and every transport (Fetch/WebSocket/LongPolling/SSE) — see Utils.ts's
            // getUserAgentHeader() and FetchHttpClient.ts. Without both allowlisted, the browser's CORS preflight
            // for /hubs/run-status fails before the connection ever starts, silently breaking the live push
            // (masked on Dashboard by its own 15s poll fallback, but fully visible on Workflow List, which has no
            // such fallback).
            // X-CSRF-Token is the double-submit CSRF header auth.interceptor.ts echoes back on state-changing
            // requests once a session cookie exists (HIPAA #7) — without it allowlisted, the preflight for any
            // such request (logout, change-password, any POST/PUT/PATCH/DELETE) fails before it reaches the
            // server, surfacing to the user as a generic CORS error.
            .WithHeaders("Authorization", "Content-Type", "Accept", "X-Correlation-Id", "X-Requested-With", "X-SignalR-User-Agent", "X-CSRF-Token")
            .AllowCredentials()
            .Build();
    }
}
