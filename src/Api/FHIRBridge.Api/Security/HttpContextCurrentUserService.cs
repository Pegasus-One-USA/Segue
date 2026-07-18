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

            // IP/user-agent/correlation are ambient request context, not identity — captured even for
            // anonymous requests (e.g. a failed login) since those are exactly the events a security
            // investigation needs this data for.
            var ipAddress = httpContext?.Connection.RemoteIpAddress?.ToString();
            var userAgent = httpContext?.Request.Headers.UserAgent.ToString();
            var correlationId = httpContext?.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                ?? httpContext?.TraceIdentifier;

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return new CurrentUserInfo(null, null, null, [], false,
                    IpAddress: ipAddress, UserAgent: userAgent, CorrelationId: correlationId);
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
                CorrelationId: correlationId);
        }
    }
}
