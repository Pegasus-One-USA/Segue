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

            return new CurrentUserInfo(
                CurrentUserClaimReader.GetExternalUserId(principal),
                CurrentUserClaimReader.GetEmail(principal),
                CurrentUserClaimReader.GetDisplayName(principal),
                CurrentUserClaimReader.GetRoles(principal),
                true);
        }
    }
}
