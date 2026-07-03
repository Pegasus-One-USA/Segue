using FHIRBridge.Application.DTOs;

namespace FHIRBridge.Application.Services;

public interface IUserAccessService
{
    Task<UserProfileDto> GetCurrentUserProfileAsync(CancellationToken cancellationToken);

    Task<UserProfileDto> RecordLoginAsync(CancellationToken cancellationToken);
}
