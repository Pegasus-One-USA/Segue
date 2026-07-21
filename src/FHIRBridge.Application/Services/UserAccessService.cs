using FHIRBridge.Application.Abstractions.Persistence;
using FHIRBridge.Application.Abstractions.Security;
using FHIRBridge.Application.DTOs;
using FHIRBridge.Application.Security;
using FHIRBridge.Domain.Entities;

namespace FHIRBridge.Application.Services;

public sealed class UserAccessService : IUserAccessService
{
    private readonly IUserAccessRepository _repository;
    private readonly ICurrentUserService _currentUserService;

    public UserAccessService(
        IUserAccessRepository repository,
        ICurrentUserService currentUserService)
    {
        _repository = repository;
        _currentUserService = currentUserService;
    }

    public async Task<UserProfileDto> GetCurrentUserProfileAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: false, cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    public async Task<UserProfileDto> RecordLoginAsync(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(recordLogin: true, cancellationToken);

        return await ToProfileDtoAsync(user, cancellationToken);
    }

    private async Task<User> GetOrCreateCurrentUserAsync(
        bool recordLogin,
        CancellationToken cancellationToken)
    {
        var currentUser = _currentUserService.CurrentUser;
        if (!currentUser.IsAuthenticated || string.IsNullOrWhiteSpace(currentUser.ExternalUserId))
        {
            throw new InvalidOperationException("Your session is no longer valid. Please sign in again.");
        }

        var user = await _repository.GetUserByExternalIdAsync(currentUser.ExternalUserId, cancellationToken);
        if (user is null)
        {
            user = new User(currentUser.ExternalUserId, currentUser.Email, currentUser.DisplayName);
            await _repository.AddUserAsync(user, cancellationToken);
        }
        else
        {
            user.UpdateProfile(currentUser.Email, currentUser.DisplayName);
            if (recordLogin)
            {
                user.RecordLogin();
            }

            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        if (recordLogin && user.LastLoginOnUtc is null)
        {
            user.RecordLogin();
            await _repository.UpdateUserAsync(user, cancellationToken);
        }

        return user;
    }

    private Task<UserProfileDto> ToProfileDtoAsync(
        User user,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(new UserProfileDto(
            user.Id,
            user.ExternalUserId,
            user.Email,
            user.DisplayName,
            _currentUserService.CurrentUser.Roles));
    }
}
