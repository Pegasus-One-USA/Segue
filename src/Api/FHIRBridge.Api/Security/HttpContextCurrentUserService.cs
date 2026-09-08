using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;

namespace FHIRBridge.Api.Security;

public sealed class HttpContextCurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IAmbientActorContext _ambientActorContext;

    public HttpContextCurrentUserService(IHttpContextAccessor httpContextAccessor, IAmbientActorContext ambientActorContext)
    {
        _httpContextAccessor = httpContextAccessor;
        _ambientActorContext = ambientActorContext;
    }

    public CurrentUserInfo CurrentUser
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var principal = httpContext?.User;

            // IP/user-agent/correlation are ambient request context, not identity — captured even for
            // anonymous requests (e.g. a failed login) since those are exactly the events a security
            // investigation needs this data for. Outside any HTTP request (a workflow run's fire-and-forget
            // background Task.Run — see WorkflowEndpoints' /run endpoint) there is no HttpContext at all, so
            // fall back to the ambient run correlation id that background execution explicitly sets via
            // IAmbientActorContext.BeginScope, rather than silently losing it.
            var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
            var correlationId = httpContext?.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                ?? httpContext?.TraceIdentifier
                ?? _ambientActorContext.CorrelationId;
            var requestOrigin = ResolveRequestOrigin(httpContext);

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new CurrentUserInfo(null, null, null, [], false,
                    IpAddress: ipAddress, UserAgent: userAgent, CorrelationId: correlationId, RequestOrigin: requestOrigin);
            }

            var permissions = principal.Claims
                .Where(c => string.Equals(c.Type, "permissions", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Value)
                .ToArray();

            return new CurrentUserInfo(
                CurrentUserClaimReader.GetExternalUserId(principal),
                CurrentUserClaimReader.GetEmail(principal),
                CurrentUserClaimReader.GetDisplayName(principal),
                CurrentUserClaimReader.GetRoles(principal),
                true,
                permissions,
                IpAddress: ipAddress,
                UserAgent: userAgent,
                CorrelationId: correlationId,
                RequestOrigin: requestOrigin,
                UserId: CurrentUserClaimReader.GetUserId(principal));
        }
    }

    // Origin is what browsers actually send on same-origin POST/PUT/DELETE (not just cross-origin ones —
    // see the Fetch spec's "Origin header" algorithm), so this is the primary source; Referer's
    // scheme+authority is the fallback for a caller/browser combination that omits Origin. Neither is
    // attested — the caller (UserManagementService/LocalAuthService) must validate this against
    // IAllowedCorsOriginsCache before trusting it for anything.
    private static string? ResolveRequestOrigin(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var origin = httpContext.Request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(origin))
        {
            return origin;
        }

        var referer = httpContext.Request.Headers.Referer.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refererUri))
        {
            return refererUri.GetLeftPart(UriPartial.Authority);
        }

        return null;
    }
}
