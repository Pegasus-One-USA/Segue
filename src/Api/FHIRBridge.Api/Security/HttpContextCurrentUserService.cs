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
            var principal = _httpContextAccessor.HttpContext?.User;
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
                permissions);
        }
    }
}
