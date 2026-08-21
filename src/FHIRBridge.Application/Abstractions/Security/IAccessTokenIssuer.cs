using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Security;

public interface IAccessTokenIssuer
{
    // No permissionCodes parameter — permissions are no longer embedded as JWT claims (see
    // IUserPermissionsProvider). A role's permission count can now run into the hundreds, which
    // overflowed the ~4096-byte browser cookie limit for the access-token cookie once it happened.
    AccessTokenDto Issue(
        User user,
        IReadOnlyCollection<string> roleNames);

    (string TokenHash, DateTime ExpiresOnUtc) IssueRefreshToken();
}
