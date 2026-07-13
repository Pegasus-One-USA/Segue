using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.Security;

namespace FHIRBridge.Api.Security;

public sealed class HttpContextCurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public CurrentUserInfo CurrentUser
    {
        get
        {
            var httpContext = _httpContextAccessor.HttpContext;
            var principal = httpContext?.User;
            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new CurrentUserInfo(null, null, null, [], false);
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
                IpAddress: httpContext?.Connection.RemoteIpAddress?.ToString(),
                UserAgent: httpContext?.Request.Headers.UserAgent.ToString(),
                // Prefer an incoming correlation header (set by a caller/gateway that already tracks one across
                // hops) over ASP.NET Core's own per-request TraceIdentifier, so a multi-service trace stays joined.
                CorrelationId: httpContext?.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                    ?? httpContext?.TraceIdentifier);
        }
    }
}
