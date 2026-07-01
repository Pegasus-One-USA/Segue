using FHIRBridge.Application.DTOs;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Abstractions.Security;

public interface IAccessTokenIssuer
{
    AccessTokenDto Issue(User user, IReadOnlyCollection<string> roleNames);
}
