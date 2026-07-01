using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Security;

public interface IAccessTokenIssuer
{
    AccessTokenDto Issue(
        User user,
        IReadOnlyCollection<string> roleNames,
        IReadOnlyCollection<string>? permissionCodes = null);

    (string TokenHash, DateTime ExpiresOnUtc) IssueRefreshToken();
}
